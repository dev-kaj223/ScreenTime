using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class NotificationContentionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly ProcessInstance Helper = new("helper", 10, 123, 1);
    private static NotificationRequest Request(TestClock clock, string key = "setup") => new(key, "helper", "Helper",
        NotificationKind.GraceFiveMinutes, clock.Now, clock.Now.AddSeconds(15), TimeSpan.FromMinutes(4));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RealReceiptInsert_HoldsWriterLock_ExpiryCommitsBeforeEnforcement_OrFailsClosed(
        bool failReceipt, bool failAuthoritativeCommit)
    {
        using var p = new TempProfile(); var clock = new TestClock();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = new DatabaseService(p.Runtime.Paths, connection => connection.CreateFunction("hold_receipt", () =>
        {
            entered.TrySetResult(); // Invoked by SQLite INSIDE the production receipt INSERT transaction.
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Receipt test gate timed out.");
            return 1;
        }));
        Assert.True(db.TryRecordNotification(Request(clock)));
        using var receipts = new SqliteConnection($"Data Source={p.Runtime.Paths.NotificationDatabasePath};Default Timeout=1");
        receipts.Open();
        receipts.Execute("CREATE TRIGGER HoldReceipt BEFORE INSERT ON NotificationReceipts BEGIN SELECT hold_receipt(); " +
            (failReceipt ? "SELECT RAISE(ABORT,'injected receipt failure'); " : "") + "END;");
        var episode = new GraceEpisode("original", "helper", DateOnly.FromDateTime(clock.Now.DateTime),
            clock.Now.AddMinutes(-16), clock.Now.AddMinutes(4), GracePhase.Active, null, [Helper]);
        db.CommitObservation([new DailyLog { Date = episode.QuotaDate,
            Entries = [new() { ProcessName = "helper", ObservedSeconds = 60, QuotaSeconds = 60 }] }], [episode]);
        using var policy = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}"); policy.Open();
        if (failAuthoritativeCommit)
            policy.Execute("CREATE TRIGGER FailExpiry BEFORE UPDATE OF Phase ON GraceEpisodes BEGIN SELECT RAISE(ABORT,'authoritative commit failed'); END;");
        var terminator = new InspectingTerminator(db, () => !release.IsSet);
        await using var monitor = new MonitorService(db, new(), new() { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 1 }] },
            processes: new FakeProcesses(() => [Helper]), terminator: terminator, time: clock);
        Task? expiry = null;
        try
        {
            await monitor.TickAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Prove a real write lock is held: another writer to the receipt file gets SQLITE_BUSY.
            var busy = Assert.Throws<SqliteException>(() => { using var transaction = receipts.BeginTransaction(); });
            Assert.Equal(5, busy.SqliteErrorCode);
            Assert.False(release.IsSet);
            clock.Now = episode.ExpiresAtUtc;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            expiry = Task.Run(() => monitor.TickAsync());
            if (failAuthoritativeCommit)
            {
                await Assert.ThrowsAsync<SqliteException>(() => expiry.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Empty(terminator.Observations);
                Assert.Equal(GracePhase.Active, db.LoadGraceEpisodes().Single().Phase);
            }
            else
            {
                await expiry.WaitAsync(TimeSpan.FromSeconds(3));
                var observation = Assert.Single(terminator.Observations);
                Assert.True(observation.ReceiptStillBlocked);
                Assert.Equal(GracePhase.Expired, observation.Episode.Phase);
                Assert.Equal(episode.ExpiresAtUtc, observation.Episode.EndedAtUtc);
                Assert.Equal(episode.ExpiresAtUtc, observation.Episode.ExpiresAtUtc);
                Assert.Equal(TerminationOutcome.Terminated, Assert.Single(monitor.EnforcementResults).Outcome);
            }
            output.WriteLine($"Receipt writer remained locked; expiry tick completed in {elapsed.Elapsed.TotalMilliseconds:F1} ms; receipt failure={failReceipt}, authoritative failure={failAuthoritativeCommit}.");
            Assert.False(monitor.NotificationWorkForTesting.IsCompleted);
            Assert.Equal(0, policy.ExecuteScalar<int>("SELECT count(*) FROM NotificationReceipts"));
        }
        finally
        {
            release.Set();
            if (expiry is not null) { try { await expiry; } catch (SqliteException) when (failAuthoritativeCommit) { } }
            await monitor.NotificationWorkForTesting;
        }
        Assert.Null(monitor.LastFault); // Manual driver exposes authoritative failures to its caller.
        Assert.Equal(failReceipt ? 1 : 2, receipts.ExecuteScalar<int>("SELECT count(*) FROM NotificationReceipts"));
    }

    private sealed class InspectingTerminator(DatabaseService db, Func<bool> receiptBlocked) : IProcessTerminator
    {
        internal List<(GraceEpisode Episode, bool ReceiptStillBlocked)> Observations { get; } = [];
        public Task<TerminationResult> TerminateAsync(ProcessInstance instance, CancellationToken cancellationToken)
        {
            Observations.Add((Assert.Single(db.LoadGraceEpisodes()), receiptBlocked()));
            return Task.FromResult(new TerminationResult(instance, TerminationOutcome.Terminated));
        }
    }

    [Fact]
    public void ExistingSchemaFourReceipts_ImportOnceTransactionally_PreserveRestartDedupAndSource()
    {
        using var p = new TempProfile(); var clock = new TestClock(); var db = new DatabaseService(p.Runtime.Paths);
        using var source = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}"); source.Open();
        source.Execute("INSERT INTO NotificationReceipts VALUES('legacy','helper',3,@ticks)", new { ticks = clock.Now.UtcTicks });
        Assert.False(db.TryRecordNotification(Request(clock, "legacy")));
        Assert.True(db.TryRecordNotification(Request(clock, "new")));
        using var target = new SqliteConnection($"Data Source={p.Runtime.Paths.NotificationDatabasePath}"); target.Open();
        Assert.Equal(1, target.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(2, target.ExecuteScalar<int>("SELECT count(*) FROM NotificationReceipts"));
        // Normal receipt operation after import needs no access to the source receipt table.
        source.Execute("ALTER TABLE NotificationReceipts RENAME TO LegacyReceiptsForTest");
        var reopened = new DatabaseService(p.Runtime.Paths);
        Assert.False(reopened.TryRecordNotification(Request(clock, "legacy")));
        Assert.False(reopened.TryRecordNotification(Request(clock, "new")));
        Assert.Equal(1, source.ExecuteScalar<int>("SELECT count(*) FROM LegacyReceiptsForTest"));
        Assert.Equal(4, source.ExecuteScalar<int>("PRAGMA user_version"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReceiptInitializationFailure_PreservesSourceAndVersion_AndDoesNotPreventPolicyStorage(bool futureVersion)
    {
        using var p = new TempProfile(); var clock = new TestClock(); var db = new DatabaseService(p.Runtime.Paths);
        using var target = new SqliteConnection($"Data Source={p.Runtime.Paths.NotificationDatabasePath}"); target.Open();
        if (futureVersion) target.Execute("PRAGMA user_version=99");
        else target.Execute("CREATE TABLE NotificationReceipts(Collision INTEGER); INSERT INTO NotificationReceipts VALUES(42)");
        if (futureVersion) Assert.Throws<InvalidOperationException>(() => db.TryRecordNotification(Request(clock)));
        else Assert.Throws<SqliteException>(() => db.TryRecordNotification(Request(clock)));
        Assert.Equal(futureVersion ? 99 : 0, target.ExecuteScalar<int>("PRAGMA user_version"));
        if (!futureVersion)
        {
            Assert.Equal(42, target.ExecuteScalar<int>("SELECT Collision FROM NotificationReceipts"));
            using var backup = new SqliteConnection($"Data Source={p.Runtime.Paths.NotificationDatabasePath}.pre-v1.bak;Mode=ReadOnly");
            backup.Open(); Assert.Equal(42, backup.ExecuteScalar<int>("SELECT Collision FROM NotificationReceipts"));
        }
        db.UpsertUsageEntry(new(2026, 9, 23), new() { ProcessName = "helper", ObservedSeconds = 60, QuotaSeconds = 60 });
        Assert.Equal(60, db.LoadLog(new(2026, 9, 23)).Entries.Single().QuotaSeconds);
    }

    [Fact]
    public void ImportFailureAfterFirstRow_RollsBackDestinationSchemaAndRows_ThenRetriesWithoutLostReceipts()
    {
        using var p = new TempProfile(); var clock = new TestClock(); var db = new DatabaseService(p.Runtime.Paths);
        using var source = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}"); source.Open();
        source.Execute("""
            PRAGMA ignore_check_constraints=ON;
            INSERT INTO NotificationReceipts VALUES('valid','helper',3,1);
            INSERT INTO NotificationReceipts VALUES('invalid','helper',99,1);
            """);
        Assert.Throws<SqliteException>(() => db.TryRecordNotification(Request(clock, "valid")));
        using var target = new SqliteConnection($"Data Source={p.Runtime.Paths.NotificationDatabasePath}"); target.Open();
        Assert.Equal(0, target.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, target.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table'"));
        Assert.Equal(2, source.ExecuteScalar<int>("SELECT count(*) FROM NotificationReceipts"));
        source.Execute("DELETE FROM NotificationReceipts WHERE ReceiptKey='invalid'");
        Assert.False(db.TryRecordNotification(Request(clock, "valid")));
        Assert.Equal(1, target.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, target.ExecuteScalar<int>("SELECT count(*) FROM NotificationReceipts"));
    }
}
