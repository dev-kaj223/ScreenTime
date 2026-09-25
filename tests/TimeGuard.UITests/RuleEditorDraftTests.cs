using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.ViewModels;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>Production editor conversion paths, without WPF windows or live profiles.</summary>
public class RuleEditorDraftTests
{
    [Fact]
    public void EveryMinuteOfDay_RoundTripsTwelveHourClock_AndDailyParts()
    {
        for (var minute = 0; minute < 1440; minute++)
        {
            var parts = RuleEditorValues.ClockParts(minute);
            Assert.True(RuleEditorValues.TryClockMinute(parts.Hour, parts.Minute, parts.Meridiem, out var clock));
            Assert.Equal(minute, clock);
            Assert.True(RuleEditorValues.TryDailyMinutes((minute / 60).ToString(), (minute % 60).ToString(), out var daily));
            Assert.Equal(minute, daily);
        }
        Assert.Equal("12:00 AM", RuleEditorValues.ClockLabel(0));
        Assert.Equal("12:00 PM", RuleEditorValues.ClockLabel(720));
        Assert.Equal("11:59 PM", RuleEditorValues.ClockLabel(1439));
    }

    [Theory]
    [InlineData("0", "00", 0)]
    [InlineData("1", "30", 90)]
    [InlineData("24", "0", 1440)]
    [InlineData("35791394", "7", int.MaxValue)]
    public void DailyLimits_ExactStoredTotalRoundtrip(string hours, string minutes, int expected)
    {
        var original = new AppRule { DailyLimitMinutes = expected };
        var draft = new RuleEditorDraft(original);
        Assert.All(draft.Days, day =>
        {
            Assert.Equal(hours, day.Hours);
            Assert.Equal(int.Parse(minutes), int.Parse(day.Minutes));
        });
        Assert.True(draft.TryBuildRule("App", "APP", out var result, out _));
        Assert.All(result!.GetWeekSchedule(), day => Assert.Equal(expected, day.DailyLimitMinutes));
        Assert.Equal(expected != 0, result.HasDailyLimit);
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("-1", "0")]
    [InlineData("1.5", "0")]
    [InlineData("+1", "0")]
    [InlineData("1", "-1")]
    [InlineData("1", "60")]
    [InlineData("2147483647", "0")]
    [InlineData("35791394", "8")]
    [InlineData("999999999999999999999", "0")]
    public void DailyLimits_InvalidOrOverflow_DoesNotProduceRule(string hours, string minutes)
    {
        var draft = new RuleEditorDraft(new() { DailyLimitMinutes = 90 });
        draft.Days[0].Hours = hours; draft.Days[0].Minutes = minutes;
        Assert.False(draft.TryBuildRule("App", "app", out var result, out var error));
        Assert.Null(result); Assert.Contains("Monday", error);
    }

    [Theory]
    [InlineData(0, 0, "AM")]
    [InlineData(13, 0, "PM")]
    [InlineData(12, 60, "AM")]
    [InlineData(12, -1, "AM")]
    [InlineData(12, 0, "other")]
    public void InvalidClockParts_AreRejected(int hour, int minute, string meridiem) =>
        Assert.False(RuleEditorValues.TryClockMinute(hour, minute, meridiem, out _));

    [Fact]
    public void SevenDailyValues_LegacyBreaksAndWindows_DisabledRuleAndPeriods_ArePreserved()
    {
        var original = new AppRule { Id = 42, Enabled = false, BreakEveryMinutes = 999, BreakDurationMinutes = 9999 };
        original.SetWeekSchedule(RuleEditorDraft.Weekdays.Select((day, index) => new AppRuleDaySchedule
        { DayOfWeek = day, DailyLimitMinutes = index * 17, AllowedWindowStart = "08:00", AllowedWindowEnd = "17:00" }));
        original.BlockedPeriods = [new() { Id = 71, RuleId = 42, StartDayOfWeek = DayOfWeek.Sunday, StartMinute = 1320,
            EndMinute = 480, EndDayOffset = 1, Enabled = false }];
        var draft = new RuleEditorDraft(original);
        Assert.Contains("Disabled", Assert.Single(draft.Groups).Times);
        Assert.True(draft.TryBuildRule(" Edited ", " APP.EXE ", out var result, out _));
        Assert.Equal(42, result!.Id); Assert.False(result.Enabled);
        Assert.Equal(999, result.BreakEveryMinutes); Assert.Equal(9999, result.BreakDurationMinutes);
        Assert.Equal("Edited", result.DisplayName); Assert.Equal("app.exe", result.ProcessName);
        Assert.Equal(original.BlockedPeriods, result.BlockedPeriods);
        foreach (var day in RuleEditorDraft.Weekdays)
        {
            Assert.Equal(original.GetScheduleForDay(day).DailyLimitMinutes, result.GetScheduleForDay(day).DailyLimitMinutes);
            Assert.Equal("08:00", result.GetScheduleForDay(day).AllowedWindowStart);
            Assert.Equal("17:00", result.GetScheduleForDay(day).AllowedWindowEnd);
        }
        // Legacy break settings cannot reject a valid small quota after those controls are removed.
        draft.Days[0].Minutes = "1";
        Assert.True(draft.TryBuildRule("App", "app", out result, out _));
        Assert.Equal(1, result!.GetScheduleForDay(DayOfWeek.Monday).DailyLimitMinutes);
        Assert.Equal(0, original.GetScheduleForDay(DayOfWeek.Monday).DailyLimitMinutes);
    }

    [Fact]
    public void MultiDayAdd_EditDaysAndTimes_Remove_PreservesUnrelatedAndCancelledOriginal()
    {
        var disabled = new BlockedPeriod { Id = 10, RuleId = 4, StartDayOfWeek = DayOfWeek.Sunday,
            StartMinute = 1320, EndMinute = 60, EndDayOffset = 1, Enabled = false };
        var original = new AppRule { Id = 4, BlockedPeriods = [disabled] };
        var draft = new RuleEditorDraft(original);
        Assert.True(draft.TrySetDowntime(null, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday],
            9, 0, "AM", 5, 0, "PM", false, true, out _));
        Assert.Equal(2, draft.Groups.Count);
        var weekdays = draft.Groups.Single(g => g.First.Enabled);
        Assert.Equal("Mon, Tue, Wed", weekdays.Days);
        Assert.Equal("9:00 AM – 5:00 PM", weekdays.Times);
        Assert.True(draft.TrySetDowntime(weekdays, [DayOfWeek.Tuesday, DayOfWeek.Friday],
            10, 30, "PM", 12, 15, "AM", true, true, out _));
        Assert.True(draft.TryBuildRule("App", "app", out var result, out _));
        Assert.Equal(3, result!.BlockedPeriods.Count);
        Assert.Contains(disabled, result.BlockedPeriods);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Friday], result.BlockedPeriods.Where(p => p.Enabled).Select(p => p.StartDayOfWeek));
        Assert.All(result.BlockedPeriods.Where(p => p.Enabled), p => { Assert.Equal(1350, p.StartMinute); Assert.Equal(15, p.EndMinute); Assert.Equal(1, p.EndDayOffset); });
        Assert.Equal(disabled, Assert.Single(original.BlockedPeriods));
        draft.Remove(draft.Groups.Single(g => g.First.Enabled));
        Assert.True(draft.TryBuildRule("App", "app", out result, out _));
        Assert.Equal(disabled, Assert.Single(result!.BlockedPeriods));
    }

    [Fact]
    public void InvalidDowntimeOrNoDays_DoesNotChangeSelectedGroup()
    {
        var period = new BlockedPeriod { Id = 99, RuleId = 12, StartDayOfWeek = DayOfWeek.Monday, StartMinute = 540, EndMinute = 1020 };
        var draft = new RuleEditorDraft(new() { BlockedPeriods = [period] });
        var group = Assert.Single(draft.Groups);
        Assert.False(draft.TrySetDowntime(group, [], 9, 0, "AM", 5, 0, "PM", false, true, out _));
        Assert.False(draft.TrySetDowntime(group, [DayOfWeek.Monday], 9, 0, "AM", 9, 0, "AM", false, true, out _));
        Assert.False(draft.TrySetDowntime(group, [DayOfWeek.Monday], 10, 0, "PM", 8, 0, "AM", false, true, out _));
        Assert.False(draft.TrySetDowntime(group, [DayOfWeek.Monday], 9, 0, "AM", 5, 0, "PM", true, true, out _));
        Assert.Equal(period, Assert.Single(Assert.Single(draft.Groups).Periods));
    }

    [Fact]
    public void EditExistingGroup_RetainsPerDayIdentityAndDisabledState()
    {
        var periods = new[] { DayOfWeek.Monday, DayOfWeek.Sunday }.Select((day, index) => new BlockedPeriod
        { Id = 90 + index, RuleId = 4, StartDayOfWeek = day, StartMinute = 0, EndMinute = 720, Enabled = false }).ToList();
        var draft = new RuleEditorDraft(new() { Id = 4, BlockedPeriods = periods });
        Assert.True(draft.TrySetDowntime(Assert.Single(draft.Groups), [DayOfWeek.Monday, DayOfWeek.Sunday],
            12, 0, "AM", 12, 30, "PM", false, false, out _));
        Assert.True(draft.TryBuildRule("App", "app", out var result, out _));
        Assert.Equal(new[] { 90, 91 }, result!.BlockedPeriods.Select(p => p.Id));
        Assert.All(result.BlockedPeriods, p => { Assert.Equal(4, p.RuleId); Assert.False(p.Enabled); Assert.Equal(750, p.EndMinute); });
    }

    [Fact]
    public void FullDay_SundayWeekBoundary_AdjacentHalfOpenPolicySurvivesEditorRoundtrip()
    {
        var draft = new RuleEditorDraft(new());
        Assert.True(draft.TrySetDowntime(null, [DayOfWeek.Sunday], 12, 0, "AM", 12, 0, "AM", true, true, out _));
        Assert.True(draft.TrySetDowntime(null, [DayOfWeek.Monday], 12, 0, "AM", 8, 0, "AM", false, true, out _));
        Assert.True(draft.TryBuildRule("App", "app", out var rule, out _));
        Assert.Equal(2, rule!.BlockedPeriods.Count); // Editor preserves adjacent definitions; Core merges them.
        var evaluator = new DowntimeEvaluator(TimeZoneInfo.Utc);
        var start = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        Assert.False(evaluator.Evaluate(rule.BlockedPeriods, start.AddTicks(-1)).IsActive);
        Assert.True(evaluator.Evaluate(rule.BlockedPeriods, start).IsActive);
        Assert.Equal(start.AddHours(32), evaluator.Evaluate(rule.BlockedPeriods, start.AddHours(24)).CurrentEnd);
        Assert.False(evaluator.Evaluate(rule.BlockedPeriods, start.AddHours(32)).IsActive);
    }

    [Theory]
    [InlineData(3, 8, 1, 30, 3, 30)]
    [InlineData(11, 1, 1, 0, 2, 0)]
    public void DstLocalDefinitions_ArePassedUnchangedToExistingCoreExpansion(int month, int day, int sh, int sm, int eh, int em)
    {
        var period = new BlockedPeriod { StartDayOfWeek = DayOfWeek.Sunday, StartMinute = sh * 60 + sm, EndMinute = eh * 60 + em };
        var draft = new RuleEditorDraft(new() { BlockedPeriods = [period] });
        var s = RuleEditorValues.ClockParts(period.StartMinute); var e = RuleEditorValues.ClockParts(period.EndMinute);
        Assert.True(draft.TrySetDowntime(Assert.Single(draft.Groups), [DayOfWeek.Sunday], s.Hour, s.Minute, s.Meridiem,
            e.Hour, e.Minute, e.Meridiem, false, true, out _));
        Assert.True(draft.TryBuildRule("App", "app", out var result, out _));
        Assert.Equal(period, Assert.Single(result!.BlockedPeriods));
        var evaluator = new DowntimeEvaluator(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        var date = new DateOnly(2026, month, day);
        Assert.Equal(evaluator.Expand([period], date, date), evaluator.Expand(result.BlockedPeriods, date, date));
    }
}
