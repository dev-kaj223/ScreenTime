using System.Diagnostics;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

public class Phase3HelperTests
{
    private sealed class RunningClock : TimeProvider
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 22, 16, 59, 56, TimeSpan.Zero) + Stopwatch.GetElapsedTime(_start);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task OwnedHelper_DowntimeDenial_Reopens_UsesMeasuredSeconds_QuotaTerminates()
    {
        using var fixture = new SeededAppFixture();
        using var other = new SeededAppFixture();
        var untouched = other.LaunchHelper(headless: true);
        var denied = fixture.LaunchHelper(headless: true);
        var db = fixture.OpenDatabase();
        var rule = new AppRule { ProcessName = "screentime.testprocess", DisplayName = "Owned phase 3 helper", DailyLimitMinutes = 1,
            BlockedPeriods = [new() { StartDayOfWeek = DayOfWeek.Tuesday, StartMinute = 0, EndMinute = 480 },
                new() { StartDayOfWeek = DayOfWeek.Tuesday, StartMinute = 480, EndMinute = 1020 }] };
        db.SaveRule(rule);
        var date = new DateOnly(2026, 9, 22);
        db.UpsertUsageEntry(date, new() { ProcessName = rule.ProcessName, ObservedSeconds = 53, QuotaSeconds = 53 });
        var scope = new TestProcessScope(fixture.Runtime.Paths);
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new WindowsProcessMonitor(isAllowedTarget: scope.Contains),
            terminator: new WindowsProcessTerminator(scope.Contains), time: new RunningClock()) { GraceDurationForTesting = TimeSpan.FromSeconds(2) };
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => !Alive(denied), TimeSpan.FromSeconds(3)));
        Assert.Equal(53, db.LoadLog(date).Entries.Single().QuotaSeconds);
        Assert.False(db.LoadLog(date).Entries.Single().Blocked);
        Assert.True(SpinWait.SpinUntil(() => monitor.Decisions.SingleOrDefault()?.MayLaunch == true, TimeSpan.FromSeconds(6)));
        var allowed = fixture.LaunchHelper(headless: true);
        Assert.True(Alive(allowed));
        Assert.True(SpinWait.SpinUntil(() => db.LoadLog(date).Entries.Single().QuotaSeconds > 53, TimeSpan.FromSeconds(12)));
        Assert.True(SpinWait.SpinUntil(() => !Alive(allowed), TimeSpan.FromSeconds(6)));
        Assert.Equal(60, db.LoadLog(date).Entries.Single().QuotaSeconds);
        Assert.False(monitor.Decisions.Single().MayLaunch);
        Assert.True(Alive(untouched));
        await monitor.StopAsync();
    }

    private static bool Alive(OwnedProcessIdentity identity)
    {
        try { using var process = Process.GetProcessById(identity.Id); return identity.Matches(process); }
        catch (ArgumentException) { return false; }
    }
}
