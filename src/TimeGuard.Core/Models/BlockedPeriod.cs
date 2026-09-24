namespace TimeGuard.Models;

/// <summary>A weekly local-calendar interval [start, end), independent of allowance.</summary>
public sealed record BlockedPeriod
{
    public int Id { get; init; }
    public int RuleId { get; init; }
    public DayOfWeek StartDayOfWeek { get; init; }
    public int StartMinute { get; init; }
    public int EndMinute { get; init; }
    public int EndDayOffset { get; init; }
    public bool Enabled { get; init; } = true;
    public override string ToString() => $"{StartDayOfWeek}: {StartMinute / 60:00}:{StartMinute % 60:00}–{EndMinute / 60:00}:{EndMinute % 60:00}{(EndDayOffset == 1 ? " next day" : "")}{(Enabled ? "" : " (disabled)")}";

    public void Validate()
    {
        var duration = EndDayOffset * 1440 + EndMinute - StartMinute;
        if (!Enum.IsDefined(StartDayOfWeek) || StartMinute is < 0 or > 1439 ||
            EndMinute is < 0 or > 1439 || EndDayOffset is < 0 or > 1 || duration is <= 0 or > 1440)
            throw new ArgumentException("Downtime needs a valid weekday, HH:mm times, and a duration greater than zero and at most 24 hours. Select next day for overnight periods.");
    }
}
