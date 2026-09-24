namespace TimeGuard.Models;

/// <summary>Copied scalar facts. No mutable config, log, process handle, or UI references.</summary>
public sealed record PolicySnapshot(
    string AppKey, string DisplayName, bool Enabled, DateOnly Date, TimeOnly LocalTime,
    int DailyLimitMinutes, TimeOnly? AllowedWindowStart, TimeOnly? AllowedWindowEnd,
    double UsageMinutes, bool WarningSent, bool IsRunning)
{
    // Temporary adapter for the existing inclusive, same-day allowed-window model.
    // Blocked and overall/break state deliberately never cross this boundary.
    public static PolicySnapshot Capture(AppRule rule, DailyLog log, TimeOnly now, bool isRunning)
    {
        var key = ProcessInstance.NormalizeKey(rule.ProcessName);
        var schedule = rule.GetScheduleForDay(log.Date.DayOfWeek);
        var usage = log.Entries.FirstOrDefault(e => ProcessInstance.NormalizeKey(e.ProcessName) == key);
        return new(key, rule.DisplayName, rule.Enabled, log.Date, now,
            schedule.DailyLimitMinutes,
            schedule.HasTimeWindow ? TimeOnly.Parse(schedule.AllowedWindowStart!) : null,
            schedule.HasTimeWindow ? TimeOnly.Parse(schedule.AllowedWindowEnd!) : null,
            usage?.UsageMinutes ?? 0, usage?.WarningSent ?? false, isRunning);
    }
}
