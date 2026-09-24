using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Measured awake process-session accounting. Never reconstructs an outage.
/// Integer persistence plus in-memory fractional ticks avoids per-poll rounding drift.</summary>
public sealed class UsageAccounting(TimeProvider time, DowntimeEvaluator downtime)
{
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _previous;
    private long _timestamp;
    private HashSet<ProcessInstance> _instances = [];
    private readonly Dictionary<(string, DateOnly, bool), long> _remainders = [];

    public void Reset() { _previous = null; _instances.Clear(); }
    public void Forget(ProcessInstance instance) => _instances.Remove(instance);

    public void Observe(DateTimeOffset now, IReadOnlyCollection<ProcessInstance> instances,
        IEnumerable<AppRule> rules, Func<DateOnly, DailyLog> log, long? observationTimestamp = null)
    {
        var stamp = observationTimestamp ?? time.GetTimestamp();
        var previous = _previous;
        var continuous = instances.Where(_instances.Contains).Select(p => p.AppKey).ToHashSet();
        var elapsed = time.GetElapsedTime(_timestamp, stamp);
        _previous = now; _timestamp = stamp; _instances = instances.ToHashSet();
        if (previous is not { } from || elapsed <= TimeSpan.Zero || elapsed > MaximumObservationGap ||
            now <= from || Math.Abs(((now - from) - elapsed).TotalSeconds) > 1) return;

        foreach (var rule in rules.Where(r => r.Enabled && continuous.Contains(r.ProcessName)))
        {
            var intervals = downtime.Expand(rule.BlockedPeriods, downtime.LocalDate(from), downtime.LocalDate(now));
            var boundaries = intervals.SelectMany(i => new[] { i.Start, i.End }).Where(b => b > from && b < now).ToList();
            for (var midnight = downtime.MidnightAfter(from); midnight < now; midnight = downtime.MidnightAfter(midnight)) boundaries.Add(midnight);
            boundaries.Add(now);
            var start = from;
            long allocatedTicks = 0;
            foreach (var end in boundaries.Distinct().Order())
            {
                // Map monotonic elapsed proportionally onto known wall-clock boundaries.
                var cumulative = end == now ? elapsed.Ticks : (long)(elapsed.Ticks * ((end - from).Ticks / (double)(now - from).Ticks));
                var ticks = cumulative - allocatedTicks;
                allocatedTicks = cumulative;
                var date = downtime.LocalDate(start);
                var entry = log(date).GetOrCreate(rule.ProcessName);
                entry.ObservedSeconds += WholeSeconds(rule.ProcessName, date, false, ticks);
                if (!intervals.Any(i => i.Start <= start && start < i.End))
                {
                    var limit = rule.GetScheduleForDay(date.DayOfWeek).DailyLimitMinutes * 60L;
                    var charge = WholeSeconds(rule.ProcessName, date, true, ticks);
                    entry.QuotaSeconds += limit == 0 ? charge : Math.Min(charge, Math.Max(0, limit - entry.QuotaSeconds));
                    if (limit > 0 && entry.QuotaSeconds >= limit) _remainders.Remove((rule.ProcessName, date, true));
                }
                start = end;
            }
        }
    }

    private long WholeSeconds(string key, DateOnly date, bool quota, long ticks)
    {
        var bucket = (key, date, quota);
        var total = _remainders.GetValueOrDefault(bucket) + ticks;
        _remainders[bucket] = total % TimeSpan.TicksPerSecond;
        return total / TimeSpan.TicksPerSecond;
    }
}
