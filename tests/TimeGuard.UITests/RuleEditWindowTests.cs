using FlaUI.Core.AutomationElements;
using TimeGuard.Models;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>Actual protected Settings → rule editor → SQLite save/reload paths.</summary>
public class RuleEditWindowTests : IDisposable
{
    private readonly SeededAppFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private Window OpenSettingsWindow()
    {
        _fx.RequestSettings();
        var prompt = _fx.App.WaitForWindow(_fx.Automation, "Protected Access");
        prompt.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))[0].AsTextBox().Text = AppFixture.TestPassword;
        prompt.FindButton("Unlock").Invoke();
        return _fx.App.WaitForWindow(_fx.Automation, "ScreenTime Settings");
    }
    private Window OpenRuleEditWindow(Window settings)
    {
        settings.FindButton("➕ Add Rule").Invoke();
        return _fx.App.WaitForWindow(_fx.Automation, "Edit App Rule");
    }
    private void CloseSettings(Window settings)
    {
        Assert.True(SpinWait.SpinUntil(() => settings.IsEnabled, TimeSpan.FromSeconds(3)), "Settings remained disabled after editor closed.");
        var handle = settings.Properties.NativeWindowHandle.Value;
        settings.Close();
        Assert.True(SpinWait.SpinUntil(() => !_fx.App.GetAllTopLevelWindows(_fx.Automation)
            .Any(w => w.Properties.NativeWindowHandle.Value == handle), TimeSpan.FromSeconds(3)));
    }
    private static void Fill(Window window, string id, string value) => window.FindTextBox(id).AsTextBox().Text = value;
    private static void Days(Window window, params DayOfWeek[] selected)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
            window.FindFirstDescendant(cf => cf.ByAutomationId($"Period{day}Box")).AsCheckBox().IsChecked = selected.Contains(day);
    }
    private static void Clock(Window window, string side, int hour, int minute, string meridiem)
    {
        foreach (var (part, value) in new[] { ("Hour", hour.ToString()), ("Minute", minute.ToString("00")), ("Meridiem", meridiem) })
        {
            var box = window.FindFirstDescendant(cf => cf.ByAutomationId($"Period{side}{part}Box")).AsComboBox();
            box.Focus(); // WPF brings the owned control into the editor's scroll viewport.
            box.Select(value);
        }
    }
    private static ListBox Periods(Window window) => window.FindFirstDescendant(cf => cf.ByAutomationId("PeriodsList")).AsListBox();
    private static void NextDay(Window window, bool value) => window.FindFirstDescendant(cf => cf.ByAutomationId("PeriodNextDayBox")).AsCheckBox().IsChecked = value;
    private static void AssertGroupCount(Window window, int count) => Assert.True(SpinWait.SpinUntil(() => Periods(window).Items.Length == count, TimeSpan.FromSeconds(3)));
    private AppRule Saved(string process)
    {
        Assert.True(SpinWait.SpinUntil(() => _fx.OpenDatabase().GetRules().Any(r => r.ProcessName == process), TimeSpan.FromSeconds(3)));
        return _fx.OpenDatabase().GetRules().Single(r => r.ProcessName == process);
    }

    [Fact]
    public void SevenDailyHoursMinutes_SaveExactTotals_ZeroUnlimited_AndLegacyBreakUiAbsent()
    {
        var settings = OpenSettingsWindow(); var editor = OpenRuleEditWindow(settings);
        Fill(editor, "DisplayNameBox", "Seven days"); Fill(editor, "ProcessNameBox", "editor-seven-days");
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        for (var i = 0; i < days.Length; i++)
        {
            Fill(editor, $"{days[i]}HoursBox", i.ToString()); Fill(editor, $"{days[i]}MinutesBox", (i * 7).ToString());
        }
        Assert.Null(editor.FindFirstDescendant(cf => cf.ByAutomationId("BreakEveryBox")));
        Assert.Null(editor.FindFirstDescendant(cf => cf.ByAutomationId("BreakDurationBox")));
        Assert.Null(editor.FindTextContaining("Break Schedule"));
        editor.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-daily-editor.png"));
        editor.FindButton("Save").Invoke();
        var saved = Saved("editor-seven-days");
        for (var i = 0; i < days.Length; i++) Assert.Equal(i * 67, saved.GetScheduleForDay(days[i]).DailyLimitMinutes);
        Assert.False(saved.GetScheduleForDay(DayOfWeek.Monday).HasDailyLimit);
        CloseSettings(settings);
    }

    [Theory]
    [InlineData("-1", "0")]
    [InlineData("1", "60")]
    [InlineData("35791394", "8")]
    public void InvalidDailyParts_KeepEditorOpenAndDoNotPersist(string hours, string minutes)
    {
        var settings = OpenSettingsWindow(); var editor = OpenRuleEditWindow(settings);
        Fill(editor, "DisplayNameBox", "Invalid quota"); Fill(editor, "ProcessNameBox", "invalid-editor-quota");
        Fill(editor, "MondayHoursBox", hours); Fill(editor, "MondayMinutesBox", minutes);
        editor.FindButton("Save").Invoke();
        Assert.True(SpinWait.SpinUntil(() => editor.FindTextContaining("Monday: enter whole") is not null, TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(_fx.OpenDatabase().GetRules(), r => r.ProcessName == "invalid-editor-quota");
        editor.Close(); CloseSettings(settings);
    }

    [Fact]
    public void MultiDayDowntime_EditOvernight_AddFullSunday_RemoveUnusedGroup_SaveReload()
    {
        var settings = OpenSettingsWindow(); var editor = OpenRuleEditWindow(settings);
        Fill(editor, "DisplayNameBox", "Grouped downtime"); Fill(editor, "ProcessNameBox", "editor-groups");
        Days(editor, DayOfWeek.Monday, DayOfWeek.Wednesday);
        Clock(editor, "Start", 9, 0, "AM"); Clock(editor, "End", 5, 0, "PM");
        editor.FindButton("AddPeriodButton").Invoke(); AssertGroupCount(editor, 1);
        Assert.NotNull(editor.FindTextContaining("Mon, Wed"));
        Periods(editor).Items[0].Select(); editor.FindButton("EditPeriodButton").Invoke();
        Days(editor, DayOfWeek.Wednesday, DayOfWeek.Thursday);
        Clock(editor, "Start", 10, 30, "PM"); Clock(editor, "End", 8, 15, "AM"); NextDay(editor, true);
        editor.FindButton("Save").Invoke();
        Assert.NotNull(editor.FindTextContaining("Apply the downtime changes"));
        editor.FindButton("AddPeriodButton").Invoke(); AssertGroupCount(editor, 1);
        Days(editor, DayOfWeek.Sunday); Clock(editor, "Start", 12, 0, "AM"); Clock(editor, "End", 12, 0, "AM");
        editor.FindButton("AddPeriodButton").Invoke(); AssertGroupCount(editor, 2);
        Days(editor, DayOfWeek.Friday); Clock(editor, "Start", 12, 0, "PM"); Clock(editor, "End", 1, 0, "PM"); NextDay(editor, false);
        editor.FindButton("AddPeriodButton").Invoke(); AssertGroupCount(editor, 3);
        Periods(editor).Items[2].Select(); editor.FindButton("RemovePeriodButton").Invoke(); AssertGroupCount(editor, 2);
        editor.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-downtime-editor.png"));
        editor.FindButton("Save").Invoke();
        var saved = Saved("editor-groups");
        Assert.Equal(3, saved.BlockedPeriods.Count);
        Assert.Contains(saved.BlockedPeriods, p => p.StartDayOfWeek == DayOfWeek.Sunday && p.StartMinute == 0 && p.EndMinute == 0 && p.EndDayOffset == 1);
        Assert.All(saved.BlockedPeriods.Where(p => p.StartDayOfWeek != DayOfWeek.Sunday), p =>
        { Assert.Contains(p.StartDayOfWeek, new[] { DayOfWeek.Wednesday, DayOfWeek.Thursday }); Assert.Equal(1350, p.StartMinute); Assert.Equal(495, p.EndMinute); Assert.Equal(1, p.EndDayOffset); });
        CloseSettings(settings);
    }

    [Fact]
    public void Downtime_NoWeekdaysOrInvalidDuration_IsExplicitAndDoesNotAdd()
    {
        var settings = OpenSettingsWindow(); var editor = OpenRuleEditWindow(settings);
        Days(editor); editor.FindButton("AddPeriodButton").Invoke();
        Assert.NotNull(editor.FindTextContaining("Select at least one weekday")); AssertGroupCount(editor, 0);
        Days(editor, DayOfWeek.Sunday); Clock(editor, "Start", 8, 0, "AM"); Clock(editor, "End", 8, 0, "AM");
        editor.FindButton("AddPeriodButton").Invoke();
        Assert.NotNull(editor.FindTextContaining("Downtime must last")); AssertGroupCount(editor, 0);
        Clock(editor, "Start", 10, 0, "PM");
        editor.FindButton("AddPeriodButton").Invoke(); AssertGroupCount(editor, 0);
        Assert.NotNull(editor.FindTextContaining("Use Next day"));
        editor.Close(); CloseSettings(settings);
    }

    [Fact]
    public void ExistingRule_SaveAndReload_PreservesHiddenLegacyAndDisabledDowntime_EditCancelIsNoop()
    {
        const string name = "Preserved editor rule";
        var db = _fx.OpenDatabase();
        var original = new AppRule { DisplayName = name, ProcessName = "editor-preserved", Enabled = false,
            DailyLimitMinutes = 90, BreakEveryMinutes = 1000, BreakDurationMinutes = 2000,
            BlockedPeriods = [new() { StartDayOfWeek = DayOfWeek.Sunday, StartMinute = 1320, EndMinute = 480, EndDayOffset = 1, Enabled = false }] };
        db.SaveRule(original);
        var settings = OpenSettingsWindow();
        var grid = settings.FindFirstDescendant(cf => cf.ByAutomationId("RulesGrid"));
        grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
            .Single(row => row.FindTextContaining(name) is not null).Patterns.SelectionItem.Pattern.Select();
        settings.FindButton("✏️ Edit").Invoke();
        var editor = _fx.App.WaitForWindow(_fx.Automation, "Edit App Rule");
        Assert.Equal("1", editor.FindTextBox("MondayHoursBox").AsTextBox().Text);
        Assert.Equal("30", editor.FindTextBox("MondayMinutesBox").AsTextBox().Text);
        AssertGroupCount(editor, 1); Assert.NotNull(editor.FindTextContaining("Disabled"));
        Periods(editor).Items[0].Select(); editor.FindButton("EditPeriodButton").Invoke();
        Clock(editor, "Start", 11, 0, "PM"); editor.FindButton("CancelPeriodEditButton").Invoke();
        editor.FindButton("Save").Invoke();
        var saved = Saved("editor-preserved");
        Assert.False(saved.Enabled); Assert.Equal(1000, saved.BreakEveryMinutes); Assert.Equal(2000, saved.BreakDurationMinutes);
        Assert.Equal(90, saved.GetScheduleForDay(DayOfWeek.Monday).DailyLimitMinutes);
        var period = Assert.Single(saved.BlockedPeriods);
        Assert.False(period.Enabled); Assert.Equal(1320, period.StartMinute); Assert.Equal(480, period.EndMinute); Assert.Equal(1, period.EndDayOffset);
        CloseSettings(settings);
    }

    [Fact]
    public void Save_DuplicateCanonicalProcess_ShowsErrorWithoutCrashingOrChangingRule()
    {
        var db = _fx.OpenDatabase();
        db.SaveRule(new() { ProcessName = "duplicatehelper", DisplayName = "Original", DailyLimitMinutes = 30 });
        var settings = OpenSettingsWindow(); var editor = OpenRuleEditWindow(settings);
        Fill(editor, "DisplayNameBox", "Replacement"); Fill(editor, "ProcessNameBox", "DuplicateHelper.exe");
        editor.FindButton("Save").Invoke(); Window? error = null;
        Assert.True(SpinWait.SpinUntil(() => { error = settings.ModalWindows.FirstOrDefault(w => w.Title == "Duplicate application"); return error is not null; }, TimeSpan.FromSeconds(10)));
        Assert.NotNull(error!.FindTextContaining("already has a rule")); error!.FindButton("OK").Invoke();
        Assert.Equal("Original", db.GetRules().Single(r => r.ProcessName == "duplicatehelper").DisplayName);
        CloseSettings(settings);
    }
}
