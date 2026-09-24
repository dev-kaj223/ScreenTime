using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class Phase4PersistenceTests
{
    private static readonly DateOnly Day = new(2026, 9, 21);
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static GraceEpisode Episode() => new(Guid.NewGuid().ToString("N"), "helper", Day, Start,
        Start.AddMinutes(20), GracePhase.Active, null, Array.AsReadOnly(new[] { new ProcessInstance("helper", 1, 1, 1) }));
    private static SqliteConnection Open(TempProfile p)
    {
        var c = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath};Foreign Keys=True"); c.Open(); return c;
    }
    private static void DowngradeToTwo(TempProfile p)
    {
        _ = new DatabaseService(p.Runtime.Paths);
        using var c = Open(p);
        c.Execute("DROP TABLE GraceProcesses; DROP TABLE GraceEpisodes; ALTER TABLE DailyUsage DROP COLUMN GraceSeconds; PRAGMA user_version=2;");
    }

    [Fact] public void VersionTwoUpgrade_Backup_UsagePreserved_Idempotent_Constraints()
    {
        using var p = new TempProfile(); DowngradeToTwo(p);
        using var c = Open(p);
        c.Execute("INSERT INTO DailyUsage(Date,ProcessName,ObservedSeconds,QuotaSeconds) VALUES('2026-09-21','helper',75,60)");
        var db = new DatabaseService(p.Runtime.Paths);
        Assert.Equal(3, c.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, db.LoadLog(Day).Entries.Single().GraceSeconds);
        Assert.Equal(75, db.LoadLog(Day).Entries.Single().ObservedSeconds);
        using var backup = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}.pre-phase4.bak;Mode=ReadOnly");
        backup.Open(); Assert.Equal(2, backup.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, backup.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('DailyUsage') WHERE name='GraceSeconds'"));
        var e = Episode(); db.CommitObservation([], [e]);
        _ = new DatabaseService(p.Runtime.Paths);
        Assert.Equal(e.ExpiresAtUtc, db.LoadGraceEpisodes().Single().ExpiresAtUtc);
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE DailyUsage SET GraceSeconds=-1"));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE DailyUsage SET GraceSeconds=0.5"));
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO GraceProcesses VALUES('missing','helper',2,2,1)"));
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO GraceProcesses VALUES(@id,'other',2,2,1)", new { id = e.Id }));
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO GraceProcesses SELECT * FROM GraceProcesses"));
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO GraceEpisodes SELECT 'other',AppKey,QuotaDate,StartedAtUtcTicks,ExpiresAtUtcTicks,Phase,EndedAtUtcTicks FROM GraceEpisodes"));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE GraceEpisodes SET ExpiresAtUtcTicks=StartedAtUtcTicks"));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE GraceEpisodes SET AppKey='HELPER'"));
        Assert.Throws<SqliteException>(() => c.Execute("DELETE FROM GraceEpisodes"));
    }

    [Fact] public void MigrationFailure_RollsBackColumnAndVersion_WithBackup()
    {
        using var p = new TempProfile(); DowngradeToTwo(p);
        using var c = Open(p); c.Execute("CREATE TABLE GraceEpisodes(Collision INTEGER)");
        Assert.Equal(2, c.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('GraceEpisodes') WHERE name='Collision'"));
        Assert.Throws<SqliteException>(() => new DatabaseService(p.Runtime.Paths));
        Assert.Equal(2, c.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('DailyUsage') WHERE name='GraceSeconds'"));
        Assert.True(File.Exists(p.Runtime.Paths.DatabasePath + ".pre-phase4.bak"));
    }

    [Fact] public void CommitFailure_RollsBackUsageEpisodeAndCapture_RetryReturnsSameDeadline()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var e = Episode();
        var log = new DailyLog { Date = Day, Entries = [new() { ProcessName = "helper", ObservedSeconds = 63, QuotaSeconds = 60, GraceSeconds = 3 }] };
        using var c = Open(p);
        c.Execute("CREATE TRIGGER FailCapture BEFORE INSERT ON GraceProcesses BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<SqliteException>(() => db.CommitObservation([log], [e]));
        Assert.Empty(db.LoadGraceEpisodes()); Assert.Empty(db.LoadLog(Day).Entries);
        c.Execute("DROP TRIGGER FailCapture");
        var committed = Assert.Single(db.CommitObservation([log], [e]));
        var retry = Assert.Single(db.CommitObservation([log], [e with { Id = "retry", ExpiresAtUtc = Start.AddHours(1), Processes = [new("helper", 2, 2, 1)] }]));
        Assert.Equal(committed.Id, retry.Id); Assert.Equal(e.ExpiresAtUtc, retry.ExpiresAtUtc);
        Assert.Equal(e.Processes, retry.Processes);
        Assert.Equal(3, db.LoadLog(Day).Entries.Single().GraceSeconds);
        db.CommitObservation([], [e with { Phase = GracePhase.CompletedByExit, EndedAtUtc = Start.AddMinutes(1) }]);
        Assert.Equal(GracePhase.CompletedByExit, db.CommitObservation([], [e]).Single().Phase);
        Assert.Single(db.LoadGraceEpisodes());
        var rule = new AppRule { ProcessName = "helper" }; db.SaveRule(rule); db.DeleteRule(rule.Id);
        db.SaveRule(new() { ProcessName = "helper" });
        Assert.Equal(GracePhase.CompletedByExit, new DatabaseService(p.Runtime.Paths).LoadGraceEpisodes().Single().Phase);
    }

    [Fact] public void BadUsageAfterCapture_RollsBackAllFacts()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        Assert.Throws<ArgumentException>(() => db.CommitObservation([new() { Date = Day,
            Entries = [new() { ProcessName = "helper", ObservedSeconds = 1, GraceSeconds = 2 }] }], [Episode()]));
        Assert.Empty(db.LoadGraceEpisodes()); Assert.Empty(db.LoadLog(Day).Entries);
    }

    private sealed class OrderedTerminator(DatabaseService db) : IProcessTerminator
    {
        public bool Called { get; private set; }
        public Task<TerminationResult> TerminateAsync(ProcessInstance p, CancellationToken ct)
        {
            var e = Assert.Single(db.LoadGraceEpisodes());
            Assert.Equal(GracePhase.Expired, e.Phase); Assert.Equal(e.ExpiresAtUtc, e.EndedAtUtc);
            Called = true; return Task.FromResult(new TerminationResult(p, TerminationOutcome.Terminated));
        }
    }
    [Fact] public async Task ExpiryIsDurableBeforeExternalKill_IncludingRestartAfterCommit()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var e = Episode();
        db.CommitObservation([], [e]); var clock = new TestClock { Now = e.ExpiresAtUtc };
        var kill = new OrderedTerminator(db);
        await using var monitor = new MonitorService(db, new(), new() { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 1 }] },
            processes: new FakeProcesses(() => e.Processes), terminator: kill, time: clock);
        await monitor.TickAsync(); Assert.True(kill.Called);
        // Simulate restart after durable expiry but before a successful external kill.
        var retry = new OrderedTerminator(db);
        await using var restarted = new MonitorService(db, new(), new() { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 1 }] },
            processes: new FakeProcesses(() => e.Processes), terminator: retry, time: clock);
        await restarted.TickAsync(); Assert.True(retry.Called);
    }
}
