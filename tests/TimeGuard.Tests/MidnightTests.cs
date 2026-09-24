using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;
using static TimeGuard.Tests.DowntimeEvaluatorTests;

namespace TimeGuard.Tests;

public class MidnightTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MidnightCreatesLedger_ButDowntimeStillDenies_AdjacentEndReopens(bool sleep)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock { Now = At("2026-09-21T23:59:58") };
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 60,
            BlockedPeriods = [Period(DayOfWeek.Tuesday, 0, 480), Period(DayOfWeek.Tuesday, 480, 1020)] };
        var terminator = new FakeTerminator { Outcome = TerminationOutcome.AccessDenied };
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new FakeProcesses(() => [new("helper", 1, 1, 1)]), terminator: terminator, time: clock);
        await monitor.TickAsync();
        if (sleep) monitor.NotifySuspend();
        clock.Now = At("2026-09-22T00:00:03");
        if (sleep) monitor.NotifyResume();
        await monitor.TickAsync();
        Assert.Equal(sleep ? 0 : 2, db.LoadLog(new(2026, 9, 21)).Entries.Single().QuotaSeconds);
        var today = db.LoadLog(new(2026, 9, 22)).Entries.Single();
        Assert.Equal(0, today.QuotaSeconds);
        Assert.Equal(sleep ? 0 : 3, today.ObservedSeconds);
        Assert.False(Assert.Single(monitor.Decisions).MayLaunch);
        Assert.Equal(At("2026-09-22T17:00:00"), monitor.Decisions.Single().NextAvailability);
        clock.Now = At("2026-09-22T08:00:00");
        monitor.NotifyResume(); await monitor.TickAsync();
        Assert.False(monitor.Decisions.Single().MayLaunch);
        clock.Now = At("2026-09-22T17:00:00");
        monitor.NotifyResume(); await monitor.TickAsync();
        Assert.True(monitor.Decisions.Single().MayLaunch);
        Assert.Equal(0, db.LoadLog(new(2026, 9, 22)).Entries.Single().QuotaSeconds);
        clock.Now = clock.Now.AddSeconds(7); await monitor.TickAsync();
        Assert.Equal(7, db.LoadLog(new(2026, 9, 22)).Entries.Single().QuotaSeconds);
    }

    [Fact] public async Task ResumeRebasesEvenShortSleep_ImmediatelyEnforcesCurrentDowntime()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock { Now = At("2026-09-22T21:59:58") };
        var rule = new AppRule { ProcessName = "helper", DailyLimitMinutes = 60,
            BlockedPeriods = [Period(DayOfWeek.Tuesday, 1320, 480, 1)] };
        var terminator = new FakeTerminator();
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new FakeProcesses(() => [new("helper", 1, 1, 1)]), terminator: terminator, time: clock);
        await monitor.TickAsync(); monitor.NotifySuspend();
        clock.Now = clock.Now.AddSeconds(10); monitor.NotifyResume(); await monitor.TickAsync();
        Assert.Equal(PolicyState.TemporaryDowntime, monitor.Decisions.Single().State);
        Assert.Single(terminator.Targets);
        Assert.Equal(0, db.LoadLog(new(2026, 9, 22)).Entries.Single().QuotaSeconds);
    }

    [Fact] public async Task TimeZoneChange_RebasesAndUsesNewLocalDayAndDowntime()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock { Now = At("2026-09-22T02:00:00") };
        var rule = new AppRule { ProcessName = "helper", BlockedPeriods = [Period(DayOfWeek.Monday, 1320, 480, 1)] };
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new FakeProcesses(() => [new("helper", 1, 1, 1)]), terminator: new FakeTerminator(), time: clock);
        await monitor.TickAsync();
        clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        monitor.NotifyTimeChanged(); await monitor.TickAsync();
        Assert.False(monitor.Decisions.Single().MayLaunch);
        Assert.Equal(0, db.LoadLog(new(2026, 9, 21)).Entries.Single().QuotaSeconds);
        Assert.Equal(At("2026-09-22T12:00:00"), monitor.Decisions.Single().DowntimeEnd);
    }
    [Fact] public async Task ResumeWakesSerializedWorkerWithoutWaitingForDiscoveryPoll()
    {
        using var profile = new TempProfile();
        var clock = new TestClock();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var monitor = new MonitorService(new DatabaseService(profile.Runtime.Paths), new(), new(),
            processes: new FakeProcesses(() => { if (Interlocked.Increment(ref calls) == 2) sampled.TrySetResult(); return []; }), time: clock);
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(2)));
        monitor.NotifyResume();
        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();
    }
}
