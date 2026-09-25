using FlaUI.Core.AutomationElements;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>
/// Tests for the RuleEditWindow validation rules (Bugs 2 and 3).
/// Opened via Settings → Add Rule.
/// </summary>
public class RuleEditWindowTests : IClassFixture<SeededAppFixture>
{
    private readonly SeededAppFixture _fx;

    public RuleEditWindowTests(SeededAppFixture fx) => _fx = fx;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void CloseSettings(Window settings)
    {
        // UIA Invoke/Close can return before WPF finishes closing the modal editor.
        // Do not close its disabled owner or leave shared fixture state for the next test.
        Assert.True(SpinWait.SpinUntil(() => settings.IsEnabled, TimeSpan.FromSeconds(3)),
            "Settings remained disabled after its modal editor closed.");
        var handle = settings.Properties.NativeWindowHandle.Value;
        settings.Close();
        Assert.True(SpinWait.SpinUntil(() => !_fx.App.GetAllTopLevelWindows(_fx.Automation)
            .Any(window => window.Properties.NativeWindowHandle.Value == handle), TimeSpan.FromSeconds(3)),
            "Settings did not close before the next test.");
    }

    private FlaUI.Core.AutomationElements.Window OpenSettingsWindow()
    {
        _fx.RequestSettings();

        var prompt = _fx.App.WaitForWindow(_fx.Automation, "Parent Access");

        var passwordBoxes = prompt.FindAllDescendants(cf =>
            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
        if (passwordBoxes.Length > 0)
        {
            passwordBoxes[0].AsTextBox().Text = AppFixture.TestPassword;
        }

        prompt.FindButton("Unlock").Invoke();
        return _fx.App.WaitForWindow(_fx.Automation, "TimeGuard Settings");
    }

    private FlaUI.Core.AutomationElements.Window OpenRuleEditWindow(
        FlaUI.Core.AutomationElements.Window settings)
    {
        settings.FindButton("➕ Add Rule").Click();
        return _fx.App.WaitForWindow(_fx.Automation, "Edit App Rule");
    }

    private static void FillField(FlaUI.Core.AutomationElements.Window window,
        string automationId, string value)
    {
        window.FindTextBox(automationId).AsTextBox().Text = value;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bug 2: Break every N minutes must be less than the daily limit.
    /// Entering breakEvery = 60 with limit = 60 should show a validation error.
    /// </summary>
    [Fact]
    public void Save_ShowsError_WhenBreakEveryEqualsLimit()
    {
        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",   "TestApp");
        FillField(ruleWindow, "ProcessNameBox",   "testapp");
        FillField(ruleWindow, "MondayLimitBox",   "60");
        FillField(ruleWindow, "BreakEveryBox",    "60");   // equals limit — invalid
        FillField(ruleWindow, "BreakDurationBox", "5");

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(300);

        // Window must still be open (save was blocked)
        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.True(windows.Any(w => w.Title?.Contains("Edit App Rule") == true),
            "RuleEditWindow should remain open when validation fails.");

        // Error text must mention the break interval / daily limit
        var error = ruleWindow.FindTextContaining("Break interval");
        Assert.NotNull(error);

        ruleWindow.Close();
        CloseSettings(settings);
    }

    /// <summary>
    /// Bug 2: Break every N minutes greater than daily limit also invalid.
    /// </summary>
    [Fact]
    public void Save_ShowsError_WhenBreakEveryExceedsLimit()
    {
        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",   "TestApp");
        FillField(ruleWindow, "ProcessNameBox",   "testapp");
        FillField(ruleWindow, "MondayLimitBox",   "60");
        FillField(ruleWindow, "BreakEveryBox",    "90");   // exceeds limit — invalid
        FillField(ruleWindow, "BreakDurationBox", "5");

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(300);

        var error = ruleWindow.FindTextContaining("Break interval");
        Assert.NotNull(error);

        ruleWindow.Close();
        CloseSettings(settings);
    }

    /// <summary>
    /// Bug 2: When daily limit = 0 (no limit) and breakEvery > 0, no error should appear.
    /// </summary>
    [Fact]
    public void Save_NoError_WhenNoLimitSet_AndBreakEveryHasValue()
    {
        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",   "TestApp");
        FillField(ruleWindow, "ProcessNameBox",   "testapp-nolimit");
        FillField(ruleWindow, "MondayLimitBox",   "0");    // no limit
        FillField(ruleWindow, "BreakEveryBox",    "60");
        FillField(ruleWindow, "BreakDurationBox", "10");

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(300);

        // Window should have closed — save succeeded
        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Edit App Rule") == true),
            "RuleEditWindow should close when no daily limit is set.");

        CloseSettings(settings);
    }

    /// <summary>
    /// Bug 3: Break duration greater than break interval must show a validation error.
    /// </summary>
    [Fact]
    public void Save_ShowsError_WhenBreakDurationExceedsBreakEvery()
    {
        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",   "TestApp");
        FillField(ruleWindow, "ProcessNameBox",   "testapp");
        FillField(ruleWindow, "MondayLimitBox",   "120");
        FillField(ruleWindow, "BreakEveryBox",    "30");
        FillField(ruleWindow, "BreakDurationBox", "45");  // > breakEvery — invalid

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(300);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.True(windows.Any(w => w.Title?.Contains("Edit App Rule") == true),
            "RuleEditWindow should remain open when break duration exceeds break interval.");

        var error = ruleWindow.FindTextContaining("Break duration");
        Assert.NotNull(error);

        ruleWindow.Close();
        CloseSettings(settings);
    }

    /// <summary>
    /// Bug 3: Break duration equal to break interval is valid.
    /// </summary>
    [Fact]
    public void Save_NoError_WhenBreakDurationEqualsBreakEvery()
    {
        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",   "TestApp");
        FillField(ruleWindow, "ProcessNameBox",   "testapp-equalbreak");
        FillField(ruleWindow, "MondayLimitBox",   "120");
        FillField(ruleWindow, "BreakEveryBox",    "30");
        FillField(ruleWindow, "BreakDurationBox", "30");  // equals breakEvery — valid

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(300);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Edit App Rule") == true),
            "RuleEditWindow should close when break duration equals break interval.");

        CloseSettings(settings);
    }

    [Fact]
    public void Save_PersistsWeekdaySpecificLimitAndDowntime()
    {
        const string processName = "weekdayscheduleapp";

        var settings   = OpenSettingsWindow();
        var ruleWindow = OpenRuleEditWindow(settings);

        FillField(ruleWindow, "DisplayNameBox",          "Weekday Schedule App");
        FillField(ruleWindow, "ProcessNameBox",          processName);
        FillField(ruleWindow, "MondayLimitBox",          "60");
        FillField(ruleWindow, "WednesdayLimitBox",       "30");
        ruleWindow.FindFirstDescendant(cf => cf.ByAutomationId("PeriodDayBox")).AsComboBox().Select("Wednesday");
        FillField(ruleWindow, "PeriodStartBox", "12:00");
        FillField(ruleWindow, "PeriodEndBox", "14:00");
        ruleWindow.FindButton("Add period").Invoke();

        ruleWindow.FindButton("Save").Invoke();
        Thread.Sleep(500);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Edit App Rule") == true),
            "RuleEditWindow should close when a weekday-specific rule is valid.");

        var db = _fx.OpenDatabase();
        var saved = db.GetRules().Single(r => r.ProcessName == processName);

        Assert.Equal(60, saved.GetScheduleForDay(DayOfWeek.Monday).DailyLimitMinutes);
        Assert.Equal(30, saved.GetScheduleForDay(DayOfWeek.Wednesday).DailyLimitMinutes);
        var period = Assert.Single(saved.BlockedPeriods);
        Assert.Equal(DayOfWeek.Wednesday, period.StartDayOfWeek);
        Assert.Equal(720, period.StartMinute);
        Assert.Equal(840, period.EndMinute);

        CloseSettings(settings);
    }

    [Fact]
    public void Downtime_AddRemoveMultiplePeriods_CrossMidnightRoundtrip()
    {
        var settings = OpenSettingsWindow();
        var editor = OpenRuleEditWindow(settings);
        FillField(editor, "DisplayNameBox", "Overnight helper");
        FillField(editor, "ProcessNameBox", "overnight-helper");
        editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodDayBox")).AsComboBox().Select("Sunday");
        FillField(editor, "PeriodStartBox", "22:00");
        FillField(editor, "PeriodEndBox", "08:00");
        editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodNextDayBox")).AsCheckBox().IsChecked = true;
        editor.FindButton("Add period").Invoke();
        Assert.True(SpinWait.SpinUntil(() => editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodsList")).AsListBox().Items.Length == 1, TimeSpan.FromSeconds(3)));
        editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodDayBox")).AsComboBox().Select("Monday");
        FillField(editor, "PeriodStartBox", "08:00");
        FillField(editor, "PeriodEndBox", "17:00");
        editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodNextDayBox")).AsCheckBox().IsChecked = false;
        editor.FindButton("Add period").Invoke();
        Assert.True(SpinWait.SpinUntil(() => editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodsList")).AsListBox().Items.Length == 2, TimeSpan.FromSeconds(3)));
        FillField(editor, "PeriodStartBox", "17:00");
        FillField(editor, "PeriodEndBox", "18:00");
        editor.FindButton("Add period").Invoke();
        var list = editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodsList")).AsListBox();
        Assert.True(SpinWait.SpinUntil(() => list.Items.Length == 3, TimeSpan.FromSeconds(3)));
        list.Items[2].Select();
        editor.FindButton("Remove period").Invoke();
        Assert.True(SpinWait.SpinUntil(() => list.Items.Length == 2, TimeSpan.FromSeconds(3)));
        editor.FindButton("Save").Invoke();
        Assert.True(SpinWait.SpinUntil(() => _fx.OpenDatabase().GetRules().Any(r => r.ProcessName == "overnight-helper"), TimeSpan.FromSeconds(3)));
        var saved = _fx.OpenDatabase().GetRules().Single(r => r.ProcessName == "overnight-helper");
        Assert.Equal(2, saved.BlockedPeriods.Count);
        Assert.Contains(saved.BlockedPeriods, p => p.StartDayOfWeek == DayOfWeek.Sunday && p.EndDayOffset == 1 && p.StartMinute == 1320 && p.EndMinute == 480);
        Assert.Contains(saved.BlockedPeriods, p => p.StartDayOfWeek == DayOfWeek.Monday && p.StartMinute == 480 && p.EndMinute == 1020);
        CloseSettings(settings);
    }

    [Theory]
    [InlineData("08:00", "08:00", "duration")]
    [InlineData("22:00", "08:00", "duration")]
    [InlineData("25:00", "08:00", "HH:mm")]
    public void Downtime_InvalidInput_IsExplicitAndDoesNotAddPeriod(string start, string end, string errorText)
    {
        var settings = OpenSettingsWindow();
        var editor = OpenRuleEditWindow(settings);
        FillField(editor, "PeriodStartBox", start);
        FillField(editor, "PeriodEndBox", end);
        editor.FindButton("Add period").Invoke();
        Assert.True(SpinWait.SpinUntil(() => editor.FindTextContaining(errorText) is not null, TimeSpan.FromSeconds(2)));
        Assert.Empty(editor.FindFirstDescendant(cf => cf.ByAutomationId("PeriodsList")).AsListBox().Items);
        editor.Close(); CloseSettings(settings);
    }
    [Fact]
    public void Save_DuplicateCanonicalProcess_ShowsErrorWithoutCrashingOrChangingRule()
    {
        var db = _fx.OpenDatabase();
        db.SaveRule(new() { ProcessName = "duplicatehelper", DisplayName = "Original", DailyLimitMinutes = 30 });
        var settings = OpenSettingsWindow();
        var editor = OpenRuleEditWindow(settings);
        FillField(editor, "DisplayNameBox", "Replacement");
        FillField(editor, "ProcessNameBox", "DuplicateHelper.exe");
        editor.FindButton("Save").Invoke();
        Window? error = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            error = settings.ModalWindows.FirstOrDefault(w => w.Title == "Duplicate application");
            return error is not null;
        }, TimeSpan.FromSeconds(10)), "Expected the duplicate-rule dialog owned by Settings.");
        Assert.NotNull(error!.FindTextContaining("already has a rule"));
        error!.FindButton("OK").Invoke();
        Assert.Equal("Original", db.GetRules().Single(r => r.ProcessName == "duplicatehelper").DisplayName);
        CloseSettings(settings);
    }
}
