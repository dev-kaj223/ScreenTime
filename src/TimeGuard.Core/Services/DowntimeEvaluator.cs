using TimeGuard.Models;

namespace TimeGuard.Services;

public sealed record DowntimeInterval(DateTimeOffset Start, DateTimeOffset End);
public sealed record DowntimeFacts(bool IsActive, DateTimeOffset? CurrentEnd, DateTimeOffset? NextStart);

/// <summary>The sole weekly interval expansion/merging authority. All instants are UTC;
/// periods and quota dates use the explicitly supplied local calendar.</summary>
public sealed class DowntimeEvaluator(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;
    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    public DateTimeOffset ResolveLocal(DateTime local, bool end = false)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Nonexistent spring times advance to the first valid minute. A repeated fall
        // boundary uses the earlier start / later end, covering both occurrences.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = Zone.IsAmbiguousTime(local)
            ? (end ? Zone.GetAmbiguousTimeOffsets(local).Min() : Zone.GetAmbiguousTimeOffsets(local).Max())
            : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public DateTimeOffset MidnightAfter(DateTimeOffset instant) =>
        ResolveLocal(LocalDate(instant).AddDays(1).ToDateTime(TimeOnly.MinValue));

    public IReadOnlyList<DowntimeInterval> Expand(IEnumerable<BlockedPeriod> periods, DateOnly first, DateOnly last)
    {
        var definitions = periods.ToArray();
        foreach (var period in definitions) period.Validate();
        var expanded = new List<DowntimeInterval>();
        for (var date = first.AddDays(-1); date <= last; date = date.AddDays(1))
            foreach (var period in definitions.Where(p => p.Enabled && p.StartDayOfWeek == date.DayOfWeek))
            {
                var start = ResolveLocal(date.ToDateTime(TimeOnly.MinValue).AddMinutes(period.StartMinute));
                var end = ResolveLocal(date.AddDays(period.EndDayOffset).ToDateTime(TimeOnly.MinValue).AddMinutes(period.EndMinute), true);
                if (end > start) expanded.Add(new(start, end));
            }
        var merged = new List<DowntimeInterval>();
        foreach (var interval in expanded.OrderBy(i => i.Start))
        {
            if (merged.Count == 0 || merged[^1].End < interval.Start) merged.Add(interval);
            else if (interval.End > merged[^1].End) merged[^1] = merged[^1] with { End = interval.End };
        }
        return merged;
    }

    public DowntimeFacts Evaluate(IEnumerable<BlockedPeriod> periods, DateTimeOffset now)
    {
        var date = LocalDate(now);
        var intervals = Expand(periods, date.AddDays(-7), date.AddDays(15));
        var active = intervals.FirstOrDefault(i => i.Start <= now && now < i.End);
        // A continuous full week has no scheduled end (the expansion horizon is not an end).
        var end = active is not null && active.End < ResolveLocal(date.AddDays(14).ToDateTime(TimeOnly.MinValue)) ? active.End : (DateTimeOffset?)null;
        return new(active is not null, end, intervals.FirstOrDefault(i => i.Start > now)?.Start);
    }

    /// <summary>Projection assumes no additional use. Reads future dates explicitly, so
    /// previously persisted quota (e.g. after a clock correction) is respected.</summary>
    public DateTimeOffset? NextAvailability(AppRule rule, DateTimeOffset now, Func<DateOnly, long> quota)
    {
        var candidate = now;
        var horizon = LocalDate(now).AddDays(8);
        while (LocalDate(candidate) <= horizon)
        {
            var date = LocalDate(candidate);
            var limit = rule.GetScheduleForDay(date.DayOfWeek).DailyLimitMinutes * 60L;
            if (limit > 0 && quota(date) >= limit) { candidate = MidnightAfter(candidate); continue; }
            var facts = Evaluate(rule.BlockedPeriods, candidate);
            if (!facts.IsActive) return candidate;
            if (facts.CurrentEnd is not { } end) return null;
            candidate = end;
        }
        return null;
    }
}
