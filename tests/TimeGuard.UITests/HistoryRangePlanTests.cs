using TimeGuard.UI;
using Xunit;

namespace TimeGuard.UITests;

public class HistoryRangePlanTests
{
    [Fact]
    public void WeekMonthYear_IncludesTodayWithCorrectBoundariesAndMonthlyBuckets()
    {
        var today = new DateOnly(2026, 9, 25);
        var week = HistoryRangePlan.For(HistoryRange.Week, today);
        Assert.Equal(today.AddDays(-6), week.Start);
        Assert.Equal(today, week.Buckets.Last()); Assert.Equal(7, week.Buckets.Count);
        Assert.False(week.Monthly);

        var month = HistoryRangePlan.For(HistoryRange.Month, today);
        Assert.Equal(today.AddDays(-29), month.Start);
        Assert.Equal(today, month.Buckets.Last()); Assert.Equal(30, month.Buckets.Count);
        Assert.False(month.Monthly);

        var year = HistoryRangePlan.For(HistoryRange.Year, today);
        Assert.Equal(new DateOnly(2025, 10, 1), year.Start);
        Assert.Equal(new DateOnly(2026, 9, 1), year.Buckets.Last());
        Assert.Equal(12, year.Buckets.Count); Assert.True(year.Monthly);
        Assert.Equal(today, year.End);
    }
}
