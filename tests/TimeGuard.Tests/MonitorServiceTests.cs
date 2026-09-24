using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class MonitorServiceTests
{
    [Theory]
    [InlineData(90, true)] [InlineData(60, false)] [InlineData(0, true)]
    public async Task ReloadConfig_RecomputesPermission_FromTodaysSchedule(int newLimit, bool permitted)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock();
        db.UpsertUsageEntry(new(2026, 9, 23), new() { ProcessName = "helper", UsageMinutes = 60, Blocked = true });
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 0,
            DaySchedules = [new() { DayOfWeek = DayOfWeek.Wednesday, DailyLimitMinutes = 60 }] };
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] }, processes: new FakeProcesses(() => []), time: clock);
        await monitor.TickAsync();
        Assert.False(Assert.Single(monitor.Decisions).MayLaunch);
        rule.DaySchedules[0].DailyLimitMinutes = newLimit;
        rule.DailyLimitMinutes = 999; // Deliberately stale legacy field must not control reload.
        monitor.ReloadConfig(new() { Rules = [rule] });
        rule.DaySchedules[0].DailyLimitMinutes = 1; // Caller cannot mutate the queued private copy.
        await monitor.TickAsync();
        Assert.Equal(permitted, Assert.Single(monitor.Decisions).MayLaunch);
        Assert.True(Assert.Single(db.LoadLog(new(2026, 9, 23)).Entries).Blocked); // No flag toggles required.
    }

    [Fact]
    public async Task ReloadConfig_ResetWarningSent_WhenLimitRaisedWellAboveUsage()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        db.UpsertUsageEntry(new(2026, 9, 23), new() { ProcessName = "helper", UsageMinutes = 56, WarningSent = true });
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 60 };
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] }, processes: new FakeProcesses(() => []), time: new TestClock());
        rule.DailyLimitMinutes = 120;
        monitor.ReloadConfig(new() { Rules = [rule] });
        await monitor.TickAsync();
        Assert.False(Assert.Single(db.LoadLog(new(2026, 9, 23)).Entries).WarningSent);
    }

    [Theory]
    [InlineData(180)] [InlineData(0)]
    public async Task ReloadConfig_OverallCapRemainsInactive_WhenRaisedOrRemoved(int newCap)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        db.UpsertUsageEntry(new(2026, 9, 23), new() { ProcessName = "helper", UsageMinutes = 120 });
        var config = new AppConfig { OverallDailyLimitMinutes = 120, Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 200 }] };
        await using var monitor = new MonitorService(db, new(), config, processes: new FakeProcesses(() => []), time: new TestClock());
        await monitor.TickAsync();
        Assert.True(Assert.Single(monitor.Decisions).MayLaunch);
        config.OverallDailyLimitMinutes = newCap;
        monitor.ReloadConfig(config);
        await monitor.TickAsync();
        Assert.True(Assert.Single(monitor.Decisions).MayLaunch);
    }
}
