using TimeGuard.Services;

namespace TimeGuard.Models;

/// <summary>Immutable policy facts. Downtime and quota can be true simultaneously.</summary>
public sealed record PolicySnapshot(
    string AppKey, string DisplayName, bool Enabled, DateOnly Date,
    int DailyLimitMinutes, long QuotaSeconds, bool WarningSent, bool IsRunning,
    DowntimeFacts Downtime, DateTimeOffset? NextAvailability)
{
    public double UsageMinutes => QuotaSeconds / 60.0;

    public static PolicySnapshot Capture(AppRule rule, DailyLog log, TimeOnly now, bool isRunning) =>
        Capture(rule, log, new DateTimeOffset(log.Date.ToDateTime(now), TimeSpan.Zero), isRunning,
            new DowntimeEvaluator(TimeZoneInfo.Utc), date => date == log.Date
                ? log.Entries.FirstOrDefault(e => ProcessInstance.NormalizeKey(e.ProcessName) == ProcessInstance.NormalizeKey(rule.ProcessName))?.QuotaSeconds ?? 0 : 0);

    public static PolicySnapshot Capture(AppRule rule, DailyLog log, DateTimeOffset now, bool isRunning,
        DowntimeEvaluator evaluator, Func<DateOnly, long> quota)
    {
        var key = ProcessInstance.NormalizeKey(rule.ProcessName);
        var usage = log.Entries.FirstOrDefault(e => ProcessInstance.NormalizeKey(e.ProcessName) == key);
        return new(key, rule.DisplayName, rule.Enabled, log.Date,
            rule.GetScheduleForDay(log.Date.DayOfWeek).DailyLimitMinutes,
            usage?.QuotaSeconds ?? 0, usage?.WarningSent ?? false, isRunning,
            evaluator.Evaluate(rule.BlockedPeriods, now), rule.Enabled ? evaluator.NextAvailability(rule, now, quota) : now);
    }
}
