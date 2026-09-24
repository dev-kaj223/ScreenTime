using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class PolicyStateTests
{
    [Theory]
    [InlineData(0, true)] [InlineData(60, false)]
    public async Task OutsideThenInsideWindow_NoPersistentScheduleBan_QuotaStillApplies(double used, bool permitted)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var day = new DateOnly(2026, 9, 23);
        db.UpsertUsageEntry(day, new() { ProcessName = "helper", UsageMinutes = used });
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 60,
            AllowedWindowStart = "15:00", AllowedWindowEnd = "20:00" };
        var clock = new TestClock();
        var terminator = new FakeTerminator();
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new FakeProcesses(() => [new("helper", 1, 12345, 1)]), terminator: terminator, time: clock);
        await monitor.TickAsync();
        Assert.Equal(PolicyState.TemporaryScheduleRestriction, Assert.Single(monitor.Decisions).State);
        Assert.Single(terminator.Targets);
        var deniedUsage = Assert.Single(db.LoadLog(day).Entries);
        Assert.False(deniedUsage.Blocked);
        Assert.Equal(used, deniedUsage.UsageMinutes); // No denied-launch quota charge.
        clock.Now = clock.Now.AddHours(6);
        await monitor.TickAsync();
        Assert.Equal(permitted, Assert.Single(monitor.Decisions).MayContinue);
        Assert.Equal(permitted ? 1 : 2, terminator.Targets.Count);
    }

    [Fact]
    public async Task OnlyConfiguredAppsAccumulate_InstancesCountOnce_OverallAndBreakAreInactive()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var config = new AppConfig { OverallDailyLimitMinutes = 1, Rules = [new()
        {
            ProcessName = "helper", DailyLimitMinutes = 120, BreakEveryMinutes = 1, BreakDurationMinutes = 1
        }] };
        var day = new DateOnly(2026, 9, 23);
        db.UpsertUsageEntry(day, new() { ProcessName = "helper", UsageMinutes = 10 });
        var terminator = new FakeTerminator();
        await using var monitor = new MonitorService(db, new(), config,
            processes: new FakeProcesses(() => [new("helper", 1, 1, 1), new("helper", 2, 2, 1), new("unrelated", 3, 3, 1)]),
            terminator: terminator, time: new TestClock());
        await monitor.TickAsync();
        Assert.Equal(10 + 5.0 / 60, Assert.Single(db.LoadLog(day).Entries).UsageMinutes);
        Assert.Empty(terminator.Targets);
        // Session timestamps still use the existing wall clock in this phase.
        Assert.Equal("helper", Assert.Single(db.LoadSessionsForDay(DateOnly.FromDateTime(DateTime.Today))).ProcessName);
    }

    private sealed class Logger : IAppLogger
    {
        public List<string> Events { get; } = [];
        public void Write(string level, string eventName, Exception? exception = null) => Events.Add(eventName);
    }

    [Theory]
    [InlineData(TerminationOutcome.Terminated)] [InlineData(TerminationOutcome.AccessDenied)]
    public async Task EnforcementPrecedesNotifications_UiFailureIsContained_OutcomeRemainsVisible(TerminationOutcome outcome)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var logger = new Logger();
        var terminator = new FakeTerminator { Outcome = outcome };
        var rule = new AppRule { ProcessName = "helper", AllowedWindowStart = "15:00", AllowedWindowEnd = "20:00" };
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] }, logger,
            new FakeProcesses(() => [new("helper", 1, 1, 1), new("helper", 2, 2, 1)]), terminator, new TestClock());
        var notifications = 0;
        var enforcedBeforeNotification = 0;
        monitor.BlockRequested += (_, _) =>
        {
            enforcedBeforeNotification = terminator.Targets.Count;
            throw new InvalidOperationException("injected UI failure");
        };
        monitor.BlockRequested += (_, _) => notifications++;
        await monitor.TickAsync();
        Assert.Equal(2, enforcedBeforeNotification);
        Assert.Equal(1, notifications);
        Assert.All(monitor.EnforcementResults, result => Assert.Equal(outcome, result.Outcome));
        Assert.Contains("Enforcement" + outcome, logger.Events);
        Assert.Contains("NotificationFailed", logger.Events);
        Assert.Null(monitor.LastFault);
    }
}
