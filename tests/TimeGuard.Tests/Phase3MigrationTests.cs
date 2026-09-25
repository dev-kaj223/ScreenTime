using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;
using static TimeGuard.Tests.DowntimeEvaluatorTests;

namespace TimeGuard.Tests;

public class Phase3MigrationTests
{
    private static SqliteConnection Open(TempProfile profile)
    {
        var c = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath};Foreign Keys=True"); c.Open(); return c;
    }
    private static void MakeVersionOne(TempProfile profile)
    {
        _ = new DatabaseService(profile.Runtime.Paths);
        using var c = Open(profile);
        c.Execute("""
            DROP TABLE NotificationReceipts; DROP TABLE GraceProcesses; DROP TABLE GraceEpisodes; ALTER TABLE DailyUsage DROP COLUMN GraceSeconds; DROP TABLE BlockedPeriods;
            ALTER TABLE DailyUsage DROP COLUMN ObservedSeconds;
            ALTER TABLE DailyUsage DROP COLUMN QuotaSeconds;
            PRAGMA user_version=1;
            INSERT INTO AppRules(ProcessName,DisplayName) VALUES('helper','Helper');
            INSERT INTO AppRuleDaySchedules VALUES(1,2,60,'17:00','23:59');
            INSERT INTO AppRuleDaySchedules VALUES(1,3,60,'08:00','17:00');
            INSERT INTO DailyUsage(Date,ProcessName,UsageMins,Blocked) VALUES('2026-09-22','helper',1.125,1);
            """);
    }

    [Fact] public void Conversion_Backup_OneTimeRounding_IdempotentReopen()
    {
        using var profile = new TempProfile(); MakeVersionOne(profile);
        var db = new DatabaseService(profile.Runtime.Paths);
        var periods = db.GetRules().Single().BlockedPeriods;
        Assert.Equal(3, periods.Count);
        Assert.Contains(periods, p => p.StartDayOfWeek == DayOfWeek.Tuesday && p.StartMinute == 0 && p.EndMinute == 1020);
        Assert.Contains(periods, p => p.StartDayOfWeek == DayOfWeek.Wednesday && p.StartMinute == 1021 && p.EndMinute == 0 && p.EndDayOffset == 1);
        var usage = db.LoadLog(new(2026, 9, 22)).Entries.Single();
        Assert.Equal(68, usage.QuotaSeconds); Assert.Equal(68, usage.ObservedSeconds); Assert.True(usage.Blocked);
        Assert.Equal(periods, new DatabaseService(profile.Runtime.Paths).GetRules().Single().BlockedPeriods);
        using var backup = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath}.pre-phase3.bak;Mode=ReadOnly");
        backup.Open(); Assert.Equal(1, backup.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(1.125, backup.ExecuteScalar<double>("SELECT UsageMins FROM DailyUsage"));
    }

    [Theory]
    [InlineData("22:00", "08:00")] [InlineData("bad", "17:00")] [InlineData(null, "17:00")]
    public void InvalidLegacyWindow_RollsBackWholeMigration(string? start, string end)
    {
        using var profile = new TempProfile(); MakeVersionOne(profile);
        using var c = Open(profile);
        c.Execute("UPDATE AppRuleDaySchedules SET WindowStart=@start,WindowEnd=@end WHERE DayOfWeek=3", new { start, end });
        Assert.Throws<InvalidOperationException>(() => new DatabaseService(profile.Runtime.Paths));
        Assert.Equal(1, c.ExecuteScalar<int>("PRAGMA user_version"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE name='BlockedPeriods'"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('DailyUsage') WHERE name='QuotaSeconds'"));
        Assert.Equal(1.125, c.ExecuteScalar<double>("SELECT UsageMins FROM DailyUsage"));
    }

    [Fact] public void PeriodRoundtrip_DeleteCascade_Constraints_AndServiceValidation()
    {
        using var profile = new TempProfile(); var db = new DatabaseService(profile.Runtime.Paths);
        var rule = new AppRule { ProcessName = "helper", BlockedPeriods =
            [Period(DayOfWeek.Sunday, 1320, 480, 1), Period(DayOfWeek.Monday, 480, 1020) with { Enabled = false }] };
        db.SaveRule(rule);
        var loaded = db.GetRules().Single();
        Assert.Equal(1, loaded.BlockedPeriods[0].EndDayOffset);
        Assert.False(loaded.BlockedPeriods[1].Enabled);
        using var c = Open(profile);
        foreach (var values in new[] { "1,7,0,480,0,1", "1,1.5,0,480,0,1", "1,1,480,480,0,1", "1,1,0,1440,0,1", "1,1,0,480,2,1", "1,1,0,480,0,2", "999,1,0,480,0,1" })
            Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO BlockedPeriods(RuleId,StartDayOfWeek,StartMinute,EndMinute,EndDayOffset,Enabled) VALUES(" + values + ")"));
        rule.BlockedPeriods = [Period(DayOfWeek.Monday, 480, 480)];
        Assert.Throws<ArgumentException>(() => db.SaveRule(rule));
        Assert.Equal(2, db.GetRules().Single().BlockedPeriods.Count);
        db.DeleteRule(rule.Id);
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT count(*) FROM BlockedPeriods"));
    }

    [Fact] public void UsageCheckpointAcrossMidnight_IsAtomic_AndSecondsMustBeIntegers()
    {
        using var profile = new TempProfile(); var db = new DatabaseService(profile.Runtime.Paths);
        Assert.Throws<ArgumentException>(() => db.SaveUsage([
            new() { Date = new(2026, 9, 21), Entries = [new() { ProcessName = "helper", QuotaSeconds = 2, ObservedSeconds = 2 }] },
            new() { Date = new(2026, 9, 22), Entries = [new() { ProcessName = "helper", QuotaSeconds = -1 }] }
        ]));
        Assert.Empty(db.LoadLog(new(2026, 9, 21)).Entries);
        using var c = Open(profile);
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO DailyUsage(Date,ProcessName,QuotaSeconds) VALUES('2026-09-22','helper',1.5)"));
        Assert.Throws<SqliteException>(() => c.Execute("INSERT INTO DailyUsage(Date,ProcessName,ObservedSeconds) VALUES('2026-09-22','helper',-1)"));
    }
    [Fact] public void LegacyInstalledPath_IsRejectedBeforeOpening()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeGuard", "timeguard.db");
        Assert.Throws<InvalidOperationException>(() => new DatabaseService($"Data Source={path}"));
    }
}
