using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;
using static TimeGuard.Tests.DowntimeEvaluatorTests;

namespace TimeGuard.Tests;

public class UsageAccountingTests
{
    private readonly TestClock _clock = new();
    private readonly Dictionary<DateOnly, DailyLog> _logs = [];
    private readonly AppRule _rule = new() { ProcessName = "helper", DailyLimitMinutes = 60 };
    private static readonly ProcessInstance Helper = new("helper", 1, 1, 1);
    private DailyLog Log(DateOnly date)
    {
        if (!_logs.TryGetValue(date, out var log)) _logs[date] = log = new() { Date = date };
        return log;
    }
    private UsageEntry Entry(string date) => Log(DateOnly.Parse(date)).GetOrCreate("helper");
    private UsageAccounting Create(string start)
    {
        _clock.Now = At(start);
        return new(_clock, new(TimeZoneInfo.Utc));
    }
    private void Observe(UsageAccounting accounting, double seconds = 0, ProcessInstance[]? processes = null)
    {
        _clock.Now = _clock.Now.AddSeconds(seconds);
        accounting.Observe(_clock.Now, processes ?? [Helper], [_rule], Log);
    }

    [Theory]
    [InlineData(3)] [InlineData(7)] [InlineData(19)] [InlineData(30)]
    public void FirstObservationZero_ThenActualElapsedIncludingSlowTick(double seconds)
    {
        var accounting = Create("2026-09-22T12:00:00");
        Observe(accounting);
        Assert.Empty(_logs);
        Observe(accounting, seconds, [Helper, new("helper", 2, 2, 1), new("unrelated", 3, 3, 1)]);
        var entry = Assert.Single(Log(new(2026, 9, 22)).Entries);
        Assert.Equal((long)seconds, entry.ObservedSeconds);
        Assert.Equal((long)seconds, entry.QuotaSeconds);
    }

    [Theory]
    [InlineData("16:59:58", 0, 1020, 3)] // downtime end
    [InlineData("16:59:58", 1020, 1080, 2)] // downtime start
    [InlineData("12:00:00", 0, 1020, 0)] // denied entire interval
    public void DowntimePortions_DoNotConsumeQuota(string start, int blockStart, int blockEnd, long charged)
    {
        _rule.BlockedPeriods = [Period(DayOfWeek.Tuesday, blockStart, blockEnd)];
        var accounting = Create("2026-09-22T" + start);
        Observe(accounting); Observe(accounting, 5);
        Assert.Equal(5, Entry("2026-09-22").ObservedSeconds);
        Assert.Equal(charged, Entry("2026-09-22").QuotaSeconds);
    }

    [Fact] public void QuotaCrossing_ClampsChargeButKeepsObservedRuntime()
    {
        Entry("2026-09-22").QuotaSeconds = 3598;
        var accounting = Create("2026-09-22T12:00:00");
        Observe(accounting); Observe(accounting, 7);
        Assert.Equal(3600, Entry("2026-09-22").QuotaSeconds);
        Assert.Equal(7, Entry("2026-09-22").ObservedSeconds);
        Observe(accounting, 5);
        Assert.Equal(3600, Entry("2026-09-22").QuotaSeconds);
        Assert.Equal(12, Entry("2026-09-22").ObservedSeconds);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Midnight_SplitsDates_AndHonorsNewDayDowntime(bool downtime)
    {
        if (downtime) _rule.BlockedPeriods = [Period(DayOfWeek.Wednesday, 0, 480)];
        var accounting = Create("2026-09-22T23:59:58");
        Observe(accounting); Observe(accounting, 5);
        Assert.Equal(2, Entry("2026-09-22").QuotaSeconds);
        Assert.Equal(3, Entry("2026-09-23").ObservedSeconds);
        Assert.Equal(downtime ? 0 : 3, Entry("2026-09-23").QuotaSeconds);
    }

    [Theory]
    [InlineData(10)] [InlineData(-10)]
    public void WallClockCorrection_RebasesWithoutCharging(int correction)
    {
        var accounting = Create("2026-09-22T12:00:00");
        _clock.Timestamp = 0; Observe(accounting);
        _clock.Timestamp = 5 * TimeSpan.TicksPerSecond;
        Observe(accounting, 5 + correction);
        Assert.Empty(_logs);
        _clock.Timestamp += 5 * TimeSpan.TicksPerSecond;
        Observe(accounting, 5);
        Assert.Equal(5, Entry("2026-09-22").QuotaSeconds);
    }

    [Fact] public void MonotonicElapsedIsAuthority_ForSmallWallClockAdjustment()
    {
        var accounting = Create("2026-09-22T12:00:00");
        _clock.Timestamp = 0; Observe(accounting);
        _clock.Timestamp = 7 * TimeSpan.TicksPerSecond;
        Observe(accounting, 7.5);
        Assert.Equal(7, Entry("2026-09-22").QuotaSeconds);
    }

    [Fact] public void DstJump_DoesNotInventAnHour()
    {
        var accounting = Create("2026-03-08T06:59:58");
        accounting = new UsageAccounting(_clock, new(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
        Observe(accounting); Observe(accounting, 5);
        Assert.Equal(5, Entry("2026-03-08").QuotaSeconds);
    }
    [Fact] public void FractionsCarryWithoutRepeatedRounding_WithinEachDate()
    {
        var accounting = Create("2026-09-22T12:00:00");
        Observe(accounting);
        for (var i = 0; i < 20; i++) Observe(accounting, 0.15);
        Assert.Equal(3, Entry("2026-09-22").QuotaSeconds);
        Assert.Equal(3, Entry("2026-09-22").ObservedSeconds);
    }

    [Theory]
    [InlineData(31)] [InlineData(3600)]
    public void UnobservedGap_Rebases(double seconds)
    {
        var accounting = Create("2026-09-22T12:00:00");
        Observe(accounting); Observe(accounting, seconds);
        Assert.Empty(_logs);
        Observe(accounting, 5);
        Assert.Equal(5, _logs.Values.Single().Entries.Single().QuotaSeconds);
    }

    [Fact] public void ExitReplacementAndRestart_DoNotInventContinuousPlay()
    {
        var accounting = Create("2026-09-22T12:00:00");
        Observe(accounting); Observe(accounting, 5, []); Observe(accounting, 5);
        Assert.Empty(_logs);
        Observe(accounting, 5, [new("helper", 1, 2, 1)]);
        Assert.Empty(_logs);
        accounting.Reset(); Observe(accounting, 5);
        Assert.Empty(_logs);
    }

    [Theory]
    [InlineData(21, 60)] [InlineData(22, 60)] [InlineData(23, 60)] [InlineData(24, 60)]
    [InlineData(25, 90)] [InlineData(26, 120)] [InlineData(27, 120)]
    public void WeeklyExample_IsConfiguration_NotPolicyConstants(int day, int limit)
    {
        _rule.DaySchedules = Enum.GetValues<DayOfWeek>().Select(d => new AppRuleDaySchedule
        { DayOfWeek = d, DailyLimitMinutes = d is DayOfWeek.Saturday or DayOfWeek.Sunday ? 120 : d == DayOfWeek.Friday ? 90 : 60 }).ToList();
        var date = new DateOnly(2026, 9, day);
        var log = Log(date); log.GetOrCreate("helper").QuotaSeconds = limit * 60L;
        var engine = new RulesEngine();
        Assert.False(engine.Evaluate(PolicySnapshot.Capture(_rule, log, new(12, 0), true)).MayLaunch);
        _rule.DaySchedules.Single(d => d.DayOfWeek == date.DayOfWeek).DailyLimitMinutes = limit + 13;
        Assert.True(engine.Evaluate(PolicySnapshot.Capture(_rule, log, new(12, 0), true)).MayLaunch);
    }
}
