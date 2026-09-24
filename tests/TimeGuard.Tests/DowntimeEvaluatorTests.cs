using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class DowntimeEvaluatorTests
{
    private readonly DowntimeEvaluator _eval = new(TimeZoneInfo.Utc);
    internal static BlockedPeriod Period(DayOfWeek day, int start, int end, int offset = 0) =>
        new() { StartDayOfWeek = day, StartMinute = start, EndMinute = end, EndDayOffset = offset };
    internal static DateTimeOffset At(string value) => DateTimeOffset.Parse(value + "+00:00");

    [Theory]
    [InlineData("07:59:59", false)] [InlineData("08:00:00", true)]
    [InlineData("12:00:00", true)] [InlineData("17:00:00", false)]
    public void SameDay_ExactHalfOpenEndpoints(string time, bool blocked)
    {
        var now = At("2026-09-22T" + time);
        var facts = _eval.Evaluate([Period(DayOfWeek.Tuesday, 480, 1020)], now);
        Assert.Equal(blocked, facts.IsActive);
        if (blocked) Assert.Equal(At("2026-09-22T17:00:00"), facts.CurrentEnd);
        else Assert.NotNull(facts.NextStart);
    }

    [Theory]
    [InlineData("2026-09-27T22:00:00", true)]
    [InlineData("2026-09-28T07:59:59", true)]
    [InlineData("2026-09-28T08:00:00", false)]
    public void CrossMidnight_SundayWrap(string now, bool blocked) =>
        Assert.Equal(blocked, _eval.Evaluate([Period(DayOfWeek.Sunday, 1320, 480, 1)], At(now)).IsActive);

    [Theory]
    [InlineData(420)] [InlineData(480)] // overlapping / adjacent
    public void MergedPeriods_EndAtSeventeen_OriginalPeriodsStayIndependent(int secondStart)
    {
        var periods = new[] { Period(DayOfWeek.Tuesday, 0, 480), Period(DayOfWeek.Tuesday, secondStart, 1020) };
        var now = At("2026-09-22T00:00:00");
        Assert.Equal(At("2026-09-22T17:00:00"), _eval.Evaluate(periods, now).CurrentEnd);
        Assert.Equal(2, periods.Length);
        Assert.Equal(At("2026-09-22T17:00:00"), _eval.NextAvailability(new() { BlockedPeriods = periods.ToList() }, now, _ => 0));
    }

    [Fact] public void MultipleSeparatePeriods_ReportNextStart()
    {
        var facts = _eval.Evaluate([Period(DayOfWeek.Tuesday, 0, 480), Period(DayOfWeek.Tuesday, 600, 1020)], At("2026-09-22T09:00:00"));
        Assert.False(facts.IsActive);
        Assert.Equal(At("2026-09-22T10:00:00"), facts.NextStart);
    }

    [Fact] public void NoPeriodsOrDisabledPeriod_AreAvailable()
    {
        var now = At("2026-09-22T09:00:00");
        Assert.Equal(new(false, null, null), _eval.Evaluate([], now));
        Assert.False(_eval.Evaluate([Period(DayOfWeek.Tuesday, 0, 1020) with { Enabled = false }], now).IsActive);
        Assert.Equal(now, _eval.NextAvailability(new(), now, _ => 0));
    }

    [Fact] public void WholeWeekBlocked_HasNoInventedEndOrAvailability()
    {
        var rule = new AppRule { BlockedPeriods = Enum.GetValues<DayOfWeek>().Select(d => Period(d, 0, 0, 1)).ToList() };
        var now = At("2026-09-22T09:00:00");
        Assert.True(_eval.Evaluate(rule.BlockedPeriods, now).IsActive);
        Assert.Null(_eval.Evaluate(rule.BlockedPeriods, now).CurrentEnd);
        Assert.Null(_eval.NextAvailability(rule, now, _ => 0));
    }

    [Fact] public void QuotaExhaustion_ProjectsNextDayPastAdjacentDowntime()
    {
        var rule = new AppRule { DailyLimitMinutes = 60, BlockedPeriods =
            [Period(DayOfWeek.Wednesday, 0, 480), Period(DayOfWeek.Wednesday, 480, 1020)] };
        Assert.Equal(At("2026-09-23T17:00:00"), _eval.NextAvailability(rule, At("2026-09-22T18:00:00"), d => d.Day == 22 ? 3600 : 0));
    }

    [Theory]
    [InlineData(-1, 0, 480, 0)] [InlineData(7, 0, 480, 0)]
    [InlineData(1, -1, 480, 0)] [InlineData(1, 0, 1440, 0)]
    [InlineData(1, 480, 480, 0)] [InlineData(1, 600, 480, 0)]
    [InlineData(1, 0, 480, 2)] [InlineData(1, 0, 480, 1)]
    public void InvalidRanges_AreRejected(int day, int start, int end, int offset) =>
        Assert.Throws<ArgumentException>(() => _eval.Evaluate([Period((DayOfWeek)day, start, end, offset)], At("2026-09-22T09:00:00")));

    [Fact] public void DstBoundaries_AreExplicitAndDeterministic()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var eval = new DowntimeEvaluator(zone);
        Assert.Equal(At("2026-03-08T07:00:00"), eval.ResolveLocal(new(2026, 3, 8, 2, 30, 0)));
        Assert.Equal(At("2026-11-01T05:30:00"), eval.ResolveLocal(new(2026, 11, 1, 1, 30, 0)));
        Assert.Equal(At("2026-11-01T06:30:00"), eval.ResolveLocal(new(2026, 11, 1, 1, 30, 0), true));
        Assert.True(eval.Evaluate([Period(DayOfWeek.Sunday, 90, 105)], At("2026-11-01T06:00:00")).IsActive);
    }
}
