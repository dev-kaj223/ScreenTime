using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class Phase2MigrationTests
{
    private static long Sql(TempProfile profile, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void FreshSchemaIsVersioned_ReopeningIsIdempotent_CanonicalRulesAreUnique()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        db.SaveRule(new() { ProcessName = " HELPER.exe ", DisplayName = "Helper" });
        Assert.Equal("helper", Assert.Single(db.GetRules()).ProcessName);
        Assert.Throws<SqliteException>(() => db.SaveRule(new() { ProcessName = "Helper", DisplayName = "Duplicate" }));
        Assert.Equal(3, Sql(profile, "PRAGMA user_version"));
        Assert.Single(new DatabaseService(profile.Runtime.Paths).GetRules());
        Assert.False(File.Exists(profile.Runtime.Paths.DatabasePath + ".pre-phase2.bak"));
    }

    [Fact]
    public void UnknownNewerVersion_IsRejectedWithoutDowngrading()
    {
        using var profile = new TempProfile();
        _ = new DatabaseService(profile.Runtime.Paths);
        Sql(profile, "PRAGMA user_version=99");
        Assert.Throws<InvalidOperationException>(() => new DatabaseService(profile.Runtime.Paths));
        Assert.Equal(99, Sql(profile, "PRAGMA user_version"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UpgradeBacksUpExistingData_ConflictingCanonicalKeysRollBack(bool conflict)
    {
        using var profile = new TempProfile();
        _ = new DatabaseService(profile.Runtime.Paths);
        Sql(profile, """
            DROP TABLE GraceProcesses; DROP TABLE GraceEpisodes; ALTER TABLE DailyUsage DROP COLUMN GraceSeconds; DROP TABLE BlockedPeriods;
            ALTER TABLE DailyUsage DROP COLUMN ObservedSeconds;
            ALTER TABLE DailyUsage DROP COLUMN QuotaSeconds;
            DROP INDEX idx_rules_appkey;
            DROP INDEX idx_usage_appkey_date;
            PRAGMA user_version=0;
            INSERT INTO AppRules(ProcessName, DisplayName) VALUES('HELPER.exe', 'Original');
            INSERT INTO DailyUsage(Date, ProcessName, UsageMins, Blocked) VALUES('2026-09-23', 'HELPER.exe', 12, 1);
            """);
        if (conflict) Sql(profile, "INSERT INTO AppRules(ProcessName, DisplayName) VALUES('helper', 'Conflict')");
        if (conflict)
        {
            Assert.Throws<SqliteException>(() => new DatabaseService(profile.Runtime.Paths));
            Assert.Equal(0, Sql(profile, "PRAGMA user_version"));
            Assert.Equal(1, Sql(profile, "SELECT count(*) FROM AppRules WHERE ProcessName='HELPER.exe'"));
        }
        else
        {
            var db = new DatabaseService(profile.Runtime.Paths);
            Assert.Equal("helper", Assert.Single(db.GetRules()).ProcessName);
            var usage = Assert.Single(db.LoadLog(new(2026, 9, 23)).Entries);
            Assert.Equal(12, usage.UsageMinutes);
            Assert.True(usage.Blocked); // Retained data, never a runtime authority.
        }
        using var backup = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath}.pre-phase2.bak;Mode=ReadOnly");
        backup.Open();
        using var cmd = backup.CreateCommand();
        cmd.CommandText = "SELECT ProcessName FROM DailyUsage";
        Assert.Equal("HELPER.exe", cmd.ExecuteScalar());
    }

    [Fact]
    public void WeekdaySchedulesAreAuthoritativeOnSave_EvenWithStaleLegacyFields()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 999,
            DaySchedules = Enum.GetValues<DayOfWeek>().Select(day => new AppRuleDaySchedule
            { DayOfWeek = day, DailyLimitMinutes = day == DayOfWeek.Wednesday ? 30 : 60 }).ToList() };
        db.SaveRule(rule);
        var loaded = Assert.Single(db.GetRules());
        Assert.Equal(30, loaded.GetScheduleForDay(DayOfWeek.Wednesday).DailyLimitMinutes);
        Assert.Equal(0, loaded.DailyLimitMinutes);
    }
}
