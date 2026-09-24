using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Measured awake accounting; UTC boundaries classify monotonic runtime.</summary>
public sealed class UsageAccounting(TimeProvider time, DowntimeEvaluator downtime)
{
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _previous;
    private long _timestamp;
    private HashSet<ProcessInstance> _instances = [];
    private HashSet<ProcessInstance> _eligible = [];
    private enum UsageKind { Observed, Quota, Grace }
    private readonly Dictionary<(string, DateOnly, UsageKind), long> _remainders = [];

    public void Reset() { _previous = null; _instances.Clear(); _eligible.Clear(); }
    public void Forget(ProcessInstance instance) { _instances.Remove(instance); _eligible.Remove(instance); }
    public void SetEligible(IEnumerable<ProcessInstance> instances) => _eligible = instances.ToHashSet();
    public TimeSpan Remaining(string app, DateOnly date, long seconds) =>
        TimeSpan.FromTicks(Math.Max(0, seconds * TimeSpan.TicksPerSecond - _remainders.GetValueOrDefault((app, date, UsageKind.Quota))));

    public void Observe(DateTimeOffset now, IReadOnlyCollection<ProcessInstance> instances,
        IEnumerable<AppRule> rules, Func<DateOnly, DailyLog> log, long? observationTimestamp = null,
        IReadOnlyList<GraceEpisode>? episodes = null, Func<QuotaCrossing, GraceEpisode?>? grant = null)
    {
        var stamp = observationTimestamp ?? time.GetTimestamp();
        var previous = _previous;
        var continuous = instances.Where(_instances.Contains).ToArray();
        var elapsed = time.GetElapsedTime(_timestamp, stamp);
        _previous = now; _timestamp = stamp; _instances = instances.ToHashSet();
        if (previous is not { } from || elapsed <= TimeSpan.Zero || elapsed > MaximumObservationGap ||
            now <= from || Math.Abs(((now - from) - elapsed).TotalSeconds) > 1) return;

        foreach (var rule in rules.Where(r => r.Enabled && continuous.Any(p => p.AppKey == r.ProcessName)))
        {
            var live = continuous.Where(p => p.AppKey == rule.ProcessName).ToArray();
            var captured = live.Where(_eligible.Contains).ToArray();
            var grace = episodes?.FirstOrDefault(e => e.AppKey == rule.ProcessName && e.Phase == GracePhase.Active);
            var expiredOnly = live.All(p => episodes?.Any(e => e.Phase == GracePhase.Expired && e.Processes.Contains(p)) == true);
            var intervals = downtime.Expand(rule.BlockedPeriods, downtime.LocalDate(from), downtime.LocalDate(now));
            var boundaries = intervals.SelectMany(i => new[] { i.Start, i.End }).Where(b => b > from && b < now).ToList();
            if (grace is not null && grace.ExpiresAtUtc > from && grace.ExpiresAtUtc < now) boundaries.Add(grace.ExpiresAtUtc);
            for (var midnight = downtime.MidnightAfter(from); midnight < now; midnight = downtime.MidnightAfter(midnight)) boundaries.Add(midnight);
            boundaries.Add(now);
            var start = from;
            long allocatedTicks = 0;
            foreach (var end in boundaries.Distinct().Order())
            {
                var cumulative = end == now ? elapsed.Ticks : (long)((decimal)elapsed.Ticks * (end - from).Ticks / (now - from).Ticks);
                var ticks = cumulative - allocatedTicks;
                allocatedTicks = cumulative;
                var date = downtime.LocalDate(start);
                var entry = log(date).GetOrCreate(rule.ProcessName);
                entry.ObservedSeconds += WholeSeconds(rule.ProcessName, date, UsageKind.Observed, ticks);
                void AddGrace(long runtime, DateTimeOffset begins)
                {
                    if (grace is null || !grace.Processes.Any(live.Contains) || begins >= grace.ExpiresAtUtc) return;
                    var allowed = end <= grace.ExpiresAtUtc ? runtime :
                        (long)((decimal)runtime * (grace.ExpiresAtUtc - begins).Ticks / (end - begins).Ticks);
                    entry.GraceSeconds += WholeSeconds(rule.ProcessName, date, UsageKind.Grace, Math.Max(0, allowed));
                }
                if (grace is not null)
                {
                    if (grace.Phase == GracePhase.Active) AddGrace(ticks, start);
                }
                else if (!expiredOnly && !intervals.Any(i => i.Start <= start && start < i.End))
                {
                    var limit = rule.GetScheduleForDay(date.DayOfWeek).DailyLimitMinutes * 60L;
                    var remaining = limit == 0 ? long.MaxValue : Remaining(rule.ProcessName, date, Math.Max(0, limit - entry.QuotaSeconds)).Ticks;
                    var charged = Math.Min(ticks, remaining);
                    entry.QuotaSeconds += WholeSeconds(rule.ProcessName, date, UsageKind.Quota, charged);
                    if (limit > 0 && remaining > 0 && ticks >= remaining)
                    {
                        _remainders.Remove((rule.ProcessName, date, UsageKind.Quota));
                        var crossing = start.AddTicks((long)((decimal)(end - start).Ticks * charged / ticks));
                        // A boundary starting downtime cannot itself create an exception.
                        if (captured.Length > 0 && !downtime.Evaluate(rule.BlockedPeriods, crossing).IsActive)
                            grace = grant?.Invoke(new(rule.ProcessName, date, crossing, Array.AsReadOnly(captured)));
                        if (grace is not null) AddGrace(ticks - charged, crossing);
                    }
                }
                start = end;
            }
        }
    }

    private long WholeSeconds(string key, DateOnly date, UsageKind kind, long ticks)
    {
        var bucket = (key, date, kind);
        var total = _remainders.GetValueOrDefault(bucket) + ticks;
        _remainders[bucket] = total % TimeSpan.TicksPerSecond;
        return total / TimeSpan.TicksPerSecond;
    }
}
