using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class NotificationTests
{
    private static readonly ProcessInstance Helper = new("helper", 10, 123, 1);
    private static readonly DateOnly Day = new(2026, 9, 23);
    private static AppConfig Config() => new() { Rules = [new() { ProcessName = "helper", DisplayName = "Helper", DailyLimitMinutes = 11 }] };
    private static MonitorService Monitor(DatabaseService db, TestClock clock, FakeTerminator? terminator = null,
        AppConfig? config = null, Func<IReadOnlyList<ProcessInstance>>? processes = null) =>
        new(db, new(), config ?? Config(), processes: new FakeProcesses(processes ?? (() => [Helper])),
            terminator: terminator ?? new(), time: clock);
    private static SqliteConnection Open(TempProfile profile)
    {
        var c = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath}"); c.Open(); return c;
    }
    private static async Task<NotificationRequest> Read(MonitorService monitor, NotificationKind kind)
    {
        await monitor.NotificationWorkForTesting;
        Assert.True(monitor.TryReadNotification(out var request));
        Assert.Equal(kind, request!.Kind);
        Assert.False(monitor.TryReadNotification(out _));
        return request;
    }
    private static async Task Advance(MonitorService monitor, TestClock clock, int seconds)
    {
        for (var i = 0; i < seconds; i += 5) { clock.Now = clock.Now.AddSeconds(Math.Min(5, seconds - i)); await monitor.TickAsync(); }
    }

    [Fact] public async Task Sequence_FiresEachMilestoneOnce_RestartKeepsEpisode_DeadlineEnforcesWithUnreadNotice()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        await using (var monitor = Monitor(db, clock))
        {
            await monitor.TickAsync(); Assert.False(monitor.TryReadNotification(out _));
            await Advance(monitor, clock, 60); await Read(monitor, NotificationKind.QuotaTenMinutes);
            await Advance(monitor, clock, 300); await Read(monitor, NotificationKind.QuotaFiveMinutes);
            await Advance(monitor, clock, 300); await Read(monitor, NotificationKind.GraceStarted);
            await monitor.TickAsync(); Assert.False(monitor.TryReadNotification(out _));
        }
        var episode = Assert.Single(db.LoadGraceEpisodes()); var terminator = new FakeTerminator();
        await using var restarted = Monitor(new DatabaseService(p.Runtime.Paths), clock, terminator);
        await restarted.TickAsync(); await restarted.NotificationWorkForTesting; Assert.False(restarted.TryReadNotification(out _));
        Assert.Equal(episode.ExpiresAtUtc, Assert.Single(restarted.GraceEpisodes).ExpiresAtUtc);
        await Advance(restarted, clock, 900); await Read(restarted, NotificationKind.GraceFiveMinutes);
        await restarted.TickAsync(); Assert.False(restarted.TryReadNotification(out _));
        await Advance(restarted, clock, 300);
        Assert.Contains(Helper, terminator.Targets);
        Assert.Equal(episode.ExpiresAtUtc, Assert.Single(db.LoadGraceEpisodes()).EndedAtUtc);
        Assert.Equal(GracePhase.Expired, Assert.Single(db.LoadGraceEpisodes()).Phase);
    }

    [Fact] public async Task RestartQuotaAndSkippedThresholds_DeduplicateAndDiscardStaleQueue()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        db.UpsertUsageEntry(Day, new() { ProcessName = "helper", QuotaSeconds = 60, ObservedSeconds = 60 });
        await using (var monitor = Monitor(db, clock))
        {
            await monitor.TickAsync(); // Leave the ten-minute request unread.
            await Advance(monitor, clock, 300);
            await Read(monitor, NotificationKind.QuotaFiveMinutes); // No stale ten-minute notice.
        }
        await using var restarted = Monitor(db, clock);
        await restarted.TickAsync(); await restarted.NotificationWorkForTesting; Assert.False(restarted.TryReadNotification(out _));
        Assert.Empty(db.LoadGraceEpisodes());
    }

    [Fact] public async Task ReceiptFailureAndSuppressedDelivery_CannotPreventGraceExpiryOrRelaunchDenial()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        using var c = Open(p);
        c.Execute("CREATE TRIGGER FailNotice BEFORE INSERT ON NotificationReceipts BEGIN SELECT RAISE(ABORT,'injected notice failure'); END");
        db.UpsertUsageEntry(Day, new() { ProcessName = "helper", QuotaSeconds = 659, ObservedSeconds = 659 });
        var terminator = new FakeTerminator();
        IReadOnlyList<ProcessInstance> live = [Helper];
        await using var monitor = Monitor(db, clock, terminator, processes: () => live);
        await monitor.TickAsync(); await Advance(monitor, clock, 1);
        var episode = Assert.Single(monitor.GraceEpisodes);
        Assert.False(monitor.TryReadNotification(out _));
        var replacement = new ProcessInstance("helper", 11, 124, 1); live = [Helper, replacement];
        await monitor.TickAsync(); Assert.Contains(replacement, terminator.Targets);
        clock.Now = episode.ExpiresAtUtc;
        await monitor.TickAsync(); Assert.Contains(Helper, terminator.Targets);
        Assert.Equal(GracePhase.Expired, Assert.Single(monitor.GraceEpisodes).Phase);
        Assert.Null(monitor.LastFault);
    }

    [Fact] public async Task DowntimeDenial_DoesNotDependOnMailboxConsumption_AndDoesNotWarnQuota()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        var config = Config(); config.Rules[0].BlockedPeriods = [new() { StartDayOfWeek = Day.DayOfWeek, StartMinute = 0, EndMinute = 0, EndDayOffset = 1 }];
        var terminator = new FakeTerminator(); await using var monitor = Monitor(db, clock, terminator, config);
        await monitor.TickAsync(); await Advance(monitor, clock, 10);
        Assert.Equal(3, terminator.Targets.Count);
        await Read(monitor, NotificationKind.Blocked); Assert.Empty(monitor.GraceEpisodes);
        Assert.Equal(0, db.LoadLog(Day).Entries.Single().QuotaSeconds);
    }

    [Fact] public async Task RecoveryWithUnrecordedEpisode_ShowsActualRemaining_FinalOnlyWithinFiveMinutes()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        var episode = new GraceEpisode("original", "helper", Day, clock.Now.AddMinutes(-16), clock.Now.AddMinutes(4), GracePhase.Active, null, [Helper]);
        db.CommitObservation([], [episode]);
        await using var monitor = Monitor(db, clock);
        await monitor.TickAsync(); var request = await Read(monitor, NotificationKind.GraceFiveMinutes);
        Assert.Equal(TimeSpan.FromMinutes(4), request.Remaining);
        Assert.Equal(episode.ExpiresAtUtc, request.GraceDeadlineUtc);
        clock.Now = episode.ExpiresAtUtc;
        Assert.False(monitor.IsNotificationCurrent(request));
    }

    [Fact] public async Task ConfigReloadInvalidatesQueuedNotice_AndLaterMilestoneSuppressesEarlierAfterAllowanceRaise()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        db.UpsertUsageEntry(Day, new() { ProcessName = "helper", QuotaSeconds = 360, ObservedSeconds = 360 });
        await using var monitor = Monitor(db, clock);
        await monitor.TickAsync(); await monitor.NotificationWorkForTesting;
        var old = Assert.Single(monitor.NotificationFacts);
        var increased = Config(); increased.Rules[0].DailyLimitMinutes = 16;
        monitor.ReloadConfig(increased);
        Assert.False(monitor.IsNotificationCurrent(old));
        await monitor.TickAsync(); await monitor.NotificationWorkForTesting;
        Assert.False(monitor.TryReadNotification(out _)); // Five already consumed; do not replay ten.
        Assert.Equal(NotificationKind.QuotaTenMinutes, Assert.Single(monitor.NotificationFacts).Kind);
    }

    [Fact] public async Task BlockedReceiptWriter_CannotDelayGrantOrDeadlineEnforcement()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        db.UpsertUsageEntry(Day, new() { ProcessName = "helper", QuotaSeconds = 659, ObservedSeconds = 659 });
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ReceiptStore(db, _ => { entered.SetResult(); gate.Wait(); throw new IOException("delayed failed notice write"); });
        var terminator = new FakeTerminator();
        await using var monitor = new MonitorService(store, new(), Config(), processes: new FakeProcesses(() => [Helper]), terminator: terminator, time: clock);
        try
        {
            await monitor.TickAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.Now = clock.Now.AddSeconds(1);
            await monitor.TickAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var episode = Assert.Single(monitor.GraceEpisodes);
            clock.Now = episode.ExpiresAtUtc;
            await monitor.TickAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains(Helper, terminator.Targets);
            Assert.Equal(episode.ExpiresAtUtc, Assert.Single(db.LoadGraceEpisodes()).EndedAtUtc);
        }
        finally { gate.Set(); }
    }

    private sealed class ReceiptStore(DatabaseService db, Func<NotificationRequest, bool> receipt) : IStateStore
    {
        public bool TryRecordNotification(NotificationRequest request) => receipt(request);
        public DailyLog LoadLog(DateOnly date) => db.LoadLog(date);
        public void UpsertUsageEntry(DateOnly date, UsageEntry entry) => db.UpsertUsageEntry(date, entry);
        public void SaveUsage(IEnumerable<DailyLog> logs) => db.SaveUsage(logs);
        public IReadOnlyList<GraceEpisode> LoadGraceEpisodes() => db.LoadGraceEpisodes();
        public IReadOnlyList<GraceEpisode> CommitObservation(IEnumerable<DailyLog> logs, IEnumerable<GraceEpisode> episodes) => db.CommitObservation(logs, episodes);
        public int OpenSession(string processName, string windowTitle = "", bool isPassive = false) => db.OpenSession(processName, windowTitle, isPassive);
        public void CloseSession(int sessionId, double timeSinceBreakMins = 0) => db.CloseSession(sessionId, timeSinceBreakMins);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void SchemaThreeMigration_BackupIdempotenceOrRollback_PreservesGrace(bool collision)
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        var episode = new GraceEpisode("original", "helper", Day, clock.Now, clock.Now.AddMinutes(20), GracePhase.Active, null, [Helper]);
        db.CommitObservation([], [episode]);
        using var c = Open(p); c.Execute("DROP TABLE NotificationReceipts; PRAGMA user_version=3;");
        if (collision) c.Execute("CREATE TABLE NotificationReceipts(Collision INTEGER)");
        if (collision)
        {
            Assert.Throws<SqliteException>(() => new DatabaseService(p.Runtime.Paths));
            Assert.Equal(3, c.ExecuteScalar<int>("PRAGMA user_version"));
        }
        else
        {
            db = new DatabaseService(p.Runtime.Paths);
            var request = new NotificationRequest("original:start", "helper", "Helper", NotificationKind.GraceStarted,
                clock.Now, clock.Now.AddSeconds(15), TimeSpan.FromMinutes(20));
            Assert.True(db.TryRecordNotification(request));
            Assert.False(new DatabaseService(p.Runtime.Paths).TryRecordNotification(request));
            Assert.Equal(4, c.ExecuteScalar<int>("PRAGMA user_version"));
            Assert.Equal(episode.ExpiresAtUtc, Assert.Single(db.LoadGraceEpisodes()).ExpiresAtUtc);
        }
        using var backup = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}.pre-phase5.bak;Mode=ReadOnly");
        backup.Open(); Assert.Equal(3, backup.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(episode.ExpiresAtUtc.UtcTicks, backup.ExecuteScalar<long>("SELECT ExpiresAtUtcTicks FROM GraceEpisodes"));
    }
}
