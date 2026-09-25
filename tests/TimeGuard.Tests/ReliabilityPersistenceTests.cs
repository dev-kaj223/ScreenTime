using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class ReliabilityPersistenceTests
{
    private static SqliteConnection Open(TempProfile p)
    {
        var connection = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CredentialsAndConfig_RollBackAsOneUnit(bool passwordOnly)
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        db.SaveConfig(new() { PasswordHash = "old-hash", PasswordSalt = "old-salt", SettingsHotkey = "old-key" });
        using var c = Open(p);
        c.Execute("CREATE TRIGGER FailSalt BEFORE UPDATE ON Settings WHEN NEW.Key='PasswordSalt' BEGIN SELECT RAISE(ABORT,'fault'); END");
        Assert.Throws<SqliteException>(() =>
        {
            if (passwordOnly) db.SavePassword("new-hash", "new-salt");
            else db.SaveConfig(new() { PasswordHash = "new-hash", PasswordSalt = "new-salt", SettingsHotkey = "new-key" });
        });
        Assert.Equal(("old-hash", "old-salt"), db.LoadPassword());
        Assert.Equal("old-key", db.LoadConfig().SettingsHotkey);
        c.Execute("DROP TRIGGER FailSalt");
        db.SavePassword("new-hash", "new-salt");
        Assert.Equal(("new-hash", "new-salt"), new DatabaseService(p.Runtime.Paths).LoadPassword());
    }

    [Fact]
    public async Task ConcurrentConfigReaders_NeverObserveMixedCredentialPairs()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        db.SaveConfig(new() { PasswordHash = "0", PasswordSalt = "0", SettingsHotkey = "0" });
        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 150; i++) db.SaveConfig(new()
                { PasswordHash = i.ToString(), PasswordSalt = i.ToString(), SettingsHotkey = i.ToString() });
        });
        for (var i = 0; i < 150; i++)
        {
            var pair = db.LoadPassword(); Assert.Equal(pair.Hash, pair.Salt);
            var config = db.LoadConfig(); Assert.Equal(config.PasswordHash, config.PasswordSalt);
            Assert.Equal(config.PasswordHash, config.SettingsHotkey);
        }
        await writer;
    }

    [Fact]
    public void RuleInsertRollback_DoesNotPublishId_AndSameObjectRetries()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        using var c = Open(p);
        c.Execute("CREATE TRIGGER FailSchedule BEFORE INSERT ON AppRuleDaySchedules BEGIN SELECT RAISE(ABORT,'fault'); END");
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 12,
            BlockedPeriods = [new() { StartDayOfWeek = DayOfWeek.Monday, StartMinute = 60, EndMinute = 120 }] };
        Assert.Throws<SqliteException>(() => db.SaveRule(rule));
        Assert.Equal(0, rule.Id); Assert.Empty(db.GetRules());
        c.Execute("DROP TRIGGER FailSchedule"); db.SaveRule(rule);
        var saved = Assert.Single(db.GetRules()); Assert.Equal(rule.Id, saved.Id); Assert.True(saved.Id > 0);
        Assert.Equal(12, saved.DailyLimitMinutes); Assert.Single(saved.BlockedPeriods);
        Assert.Equal(7, c.ExecuteScalar<int>("SELECT count(*) FROM AppRuleDaySchedules"));
    }

    [Fact]
    public void FailedConnectionInitialization_DisposesConnection_AndPreservesOriginalFailure()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        var failure = new IOException("connection initialization fault");
        var failed = new List<SqliteConnection>();
        db.ConfigureConnectionForTesting = c => { failed.Add(c); throw failure; };
        for (var i = 0; i < 30; i++) Assert.Same(failure, Assert.Throws<IOException>(() => db.GetSetting("x")));
        Assert.All(failed, c => Assert.Equal(System.Data.ConnectionState.Closed, c.State));
        db.ConfigureConnectionForTesting = null;
        db.SetSetting("recovered", "yes"); Assert.Equal("yes", db.GetSetting("recovered"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CorruptOrFutureDatabase_IsPreservedWithoutReset(bool future)
    {
        using var p = new TempProfile(); Directory.CreateDirectory(p.Runtime.Paths.Root);
        if (future)
        {
            _ = new DatabaseService(p.Runtime.Paths);
            using var c = Open(p); c.Execute("PRAGMA user_version=999; PRAGMA wal_checkpoint(TRUNCATE)");
        }
        else File.WriteAllText(p.Runtime.Paths.DatabasePath, "not a sqlite database; preserve this evidence");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(p.Runtime.Paths.DatabasePath);
        for (var i = 0; i < 5; i++) Assert.NotNull(Record.Exception(() => new DatabaseService(p.Runtime.Paths)));
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(p.Runtime.Paths.DatabasePath));
    }

    [Fact]
    public void SqliteFull_RollsBackConfig_AndCanRetryAfterSpaceReturns()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        db.SavePassword("original", "salt");
        db.ConfigureConnectionForTesting = c => c.Execute("PRAGMA max_page_count=" + c.ExecuteScalar<long>("PRAGMA page_count"));
        var error = Assert.Throws<SqliteException>(() => db.SaveConfig(new()
            { PasswordHash = "replacement", PasswordSalt = "replacement", SettingsHotkey = new string('x', 1024 * 1024) }));
        Assert.Equal(13, error.SqliteErrorCode); // SQLITE_FULL from real SQLite page allocation.
        Assert.Equal(("original", "salt"), db.LoadPassword());
        db.ConfigureConnectionForTesting = c => c.Execute("PRAGMA max_page_count=1073741823");
        db.SavePassword("retry", "retry"); Assert.Equal(("retry", "retry"), db.LoadPassword());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExpiryPersistenceFault_Supervised_NoUncommittedKill_RestartUsesOriginalDeadline(bool diskFull)
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths);
        var clock = new TestClock(); var helper = new ProcessInstance("helper", 123, 456, 1);
        var episode = new GraceEpisode("original", "helper", DateOnly.FromDateTime(clock.Now.DateTime),
            clock.Now.AddMinutes(-20), clock.Now, GracePhase.Active, null, [helper]);
        db.CommitObservation([], [episode]);
        var kill = new FakeTerminator(); var config = new AppConfig { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 1 }] };
        var monitor = new MonitorService(db, new(), config, processes: new FakeProcesses(() => [helper]), terminator: kill, time: clock);
        using var c = Open(p);
        if (diskFull)
        {
            c.Execute("CREATE TRIGGER FillDisk BEFORE INSERT ON DailyUsage BEGIN INSERT INTO Settings VALUES('fill',zeroblob(1048576)); END");
            db.ConfigureConnectionForTesting = connection => connection.Execute("PRAGMA max_page_count=" + connection.ExecuteScalar<long>("PRAGMA page_count"));
        }
        using var locked = diskFull ? null : c.BeginTransaction();
        var elapsed = Stopwatch.StartNew(); monitor.Start();
        var error = await Assert.ThrowsAsync<SqliteException>(() => monitor.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(diskFull ? 13 : 5, error.SqliteErrorCode); Assert.Same(error, monitor.LastFault);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 0, 12);
        Assert.Empty(kill.Targets); Assert.Empty(monitor.Decisions);
        locked?.Rollback();
        if (diskFull) c.Execute("DROP TRIGGER FillDisk");
        db.ConfigureConnectionForTesting = connection => connection.Execute("PRAGMA max_page_count=1073741823");
        Assert.Equal(GracePhase.Active, Assert.Single(db.LoadGraceEpisodes()).Phase);
        await Assert.ThrowsAsync<SqliteException>(() => monitor.StopAsync());
        await using var restart = new MonitorService(db, new(), config, processes: new FakeProcesses(() => [helper]), terminator: kill, time: clock);
        await restart.TickAsync();
        var ended = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(episode.ExpiresAtUtc, ended.ExpiresAtUtc); Assert.Equal(episode.ExpiresAtUtc, ended.EndedAtUtc);
        Assert.Equal(GracePhase.Expired, ended.Phase); Assert.Equal(helper, Assert.Single(kill.Targets));
        Assert.Equal("ok", c.ExecuteScalar<string>("PRAGMA integrity_check"));
    }
}
