namespace TimeGuard.UI;

internal enum HistoryRange { Week, Month, Year }

internal sealed record HistoryRangePlan(DateOnly Start, DateOnly End, IReadOnlyList<DateOnly> Buckets, bool Monthly)
{
    internal static HistoryRangePlan For(HistoryRange range, DateOnly today)
    {
        var start = range switch
        {
            HistoryRange.Week => today.AddDays(-6),
            HistoryRange.Month => today.AddDays(-29),
            HistoryRange.Year => new DateOnly(today.Year, today.Month, 1).AddMonths(-11),
            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
        var count = range switch { HistoryRange.Week => 7, HistoryRange.Month => 30, _ => 12 };
        var buckets = Enumerable.Range(0, count)
            .Select(i => range == HistoryRange.Year ? start.AddMonths(i) : start.AddDays(i)).ToArray();
        return new(start, today, buckets, range == HistoryRange.Year);
    }
}
