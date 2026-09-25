using System.Globalization;
using TimeGuard.Models;

namespace TimeGuard.ViewModels;

/// <summary>Presentation conversion only. Core still expands local-calendar downtime.</summary>
internal static class RuleEditorValues
{
    public static bool TryDailyMinutes(string hours, string minutes, out int total)
    {
        total = 0;
        if (!int.TryParse(hours.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(minutes.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            m > 59 || (long)h * 60 + m > int.MaxValue) return false;
        total = h * 60 + m;
        return true;
    }

    public static (int Hour, int Minute, string Meridiem) ClockParts(int minute)
    {
        if (minute is < 0 or > 1439) throw new ArgumentOutOfRangeException(nameof(minute));
        var hour = minute / 60;
        return (hour % 12 == 0 ? 12 : hour % 12, minute % 60, hour < 12 ? "AM" : "PM");
    }

    public static bool TryClockMinute(int hour, int minute, string? meridiem, out int result)
    {
        result = 0;
        if (hour is < 1 or > 12 || minute is < 0 or > 59 || meridiem is not ("AM" or "PM")) return false;
        result = (hour % 12 + (meridiem == "PM" ? 12 : 0)) * 60 + minute;
        return true;
    }

    public static string ClockLabel(int minute)
    {
        var (h, m, meridiem) = ClockParts(minute);
        return $"{h}:{m:00} {meridiem}";
    }
}

internal sealed class DailyLimitInput(AppRuleDaySchedule schedule)
{
    public DayOfWeek Day => Original.DayOfWeek;
    public string Label => Day.ToString();
    public string HoursId => $"{Day}HoursBox";
    public string MinutesId => $"{Day}MinutesBox";
    public string Hours { get; set; } = (schedule.DailyLimitMinutes / 60).ToString(CultureInfo.InvariantCulture);
    public string Minutes { get; set; } = (schedule.DailyLimitMinutes % 60).ToString("00", CultureInfo.InvariantCulture);
    public AppRuleDaySchedule Original { get; } = schedule;
}

internal sealed class DowntimeGroup(IReadOnlyList<BlockedPeriod> periods)
{
    public IReadOnlyList<BlockedPeriod> Periods { get; } = periods;
    public BlockedPeriod First => Periods[0];
    public string Days => string.Join(", ", RuleEditorDraft.Weekdays
        .Where(day => Periods.Any(p => p.StartDayOfWeek == day)).Select(day => day.ToString()[..3]));
    public string Times => $"{RuleEditorValues.ClockLabel(First.StartMinute)} – {RuleEditorValues.ClockLabel(First.EndMinute)}" +
        (First.EndDayOffset == 1 ? " (next day)" : "") + (First.Enabled ? "" : " · Disabled");
    public override string ToString() => $"{Days}: {Times}";
}

/// <summary>A private editable copy; Cancel never mutates the supplied rule.</summary>
internal sealed class RuleEditorDraft
{
    internal static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday,
        DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private readonly AppRule _original;
    private readonly List<BlockedPeriod> _periods;
    public IReadOnlyList<DailyLimitInput> Days { get; }
    public IReadOnlyList<DowntimeGroup> Groups => _periods
        .GroupBy(p => (p.StartMinute, p.EndMinute, p.EndDayOffset, p.Enabled))
        .Select(g => new DowntimeGroup(g.ToArray())).ToArray();

    public RuleEditorDraft(AppRule original)
    {
        _original = original;
        Days = original.GetWeekSchedule().Select(s => new DailyLimitInput(s)).ToArray();
        _periods = original.BlockedPeriods.ToList(); // immutable records; keep IDs/disabled state
    }

    public bool TrySetDowntime(DowntimeGroup? replacing, IEnumerable<DayOfWeek> days,
        int startHour, int startMinute, string? startMeridiem,
        int endHour, int endMinute, string? endMeridiem, bool nextDay, bool enabled, out string error)
    {
        error = "Choose a valid start and end time.";
        if (!RuleEditorValues.TryClockMinute(startHour, startMinute, startMeridiem, out var start) ||
            !RuleEditorValues.TryClockMinute(endHour, endMinute, endMeridiem, out var end)) return false;
        var selected = days.Distinct().ToArray();
        error = "Select at least one weekday.";
        if (selected.Length == 0) return false;
        if (selected.Any(day => !Enum.IsDefined(day))) return false;
        var duration = (nextDay ? 1440 : 0) + end - start;
        error = "Downtime must last more than zero and at most 24 hours. Use Next day for overnight or full-day downtime.";
        if (duration is <= 0 or > 1440) return false;
        // Keep a separate period per selected start-day; never merge, split, convert
        // to UTC, or reinterpret adjacency/DST at this presentation boundary.
        var replacements = new List<BlockedPeriod>();
        foreach (var day in selected)
        {
            var originals = replacing?.Periods.Where(p => p.StartDayOfWeek == day).ToArray() ?? [];
            if (originals.Length == 0) originals = [new() { RuleId = _original.Id, StartDayOfWeek = day }];
            replacements.AddRange(originals.Select(p => p with { StartMinute = start, EndMinute = end,
                EndDayOffset = nextDay ? 1 : 0, Enabled = enabled }));
        }
        if (replacing is not null) Remove(replacing);
        _periods.AddRange(replacements);
        error = "";
        return true;
    }

    public void Remove(DowntimeGroup group)
    {
        foreach (var period in group.Periods) _periods.Remove(period);
    }

    public bool TryBuildRule(string displayName, string processName, out AppRule? result, out string error)
    {
        result = null;
        error = "App name and process name are required.";
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(processName)) return false;
        var schedules = new List<AppRuleDaySchedule>();
        foreach (var day in Days)
        {
            error = $"{day.Label}: enter whole hours and minutes (0–59), within the supported total of {int.MaxValue} minutes.";
            if (!RuleEditorValues.TryDailyMinutes(day.Hours, day.Minutes, out var total)) return false;
            schedules.Add(new() { DayOfWeek = day.Day, DailyLimitMinutes = total,
                AllowedWindowStart = day.Original.AllowedWindowStart, AllowedWindowEnd = day.Original.AllowedWindowEnd });
        }
        result = new() { Id = _original.Id, DisplayName = displayName.Trim(), ProcessName = processName.Trim().ToLowerInvariant(),
            Enabled = _original.Enabled, BreakEveryMinutes = _original.BreakEveryMinutes,
            BreakDurationMinutes = _original.BreakDurationMinutes, BlockedPeriods = _periods.ToList() };
        result.SetWeekSchedule(schedules);
        error = "";
        return true;
    }
}
