using FlaUI.Core.Input;
using FlaUI.Core.AutomationElements;
using System.Diagnostics;
using TimeGuard.Models;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>
/// Tests the SettingsWindow (rules management, retained legacy values, save/cancel).
/// Uses <see cref="SeededAppFixture"/> so the app starts with a known password
/// and the first-run window is skipped.
/// </summary>
public class SettingsWindowTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly SeededAppFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Signals this fixture's settings event, types the password in the
    /// PasswordPromptWindow, clicks Unlock, then waits for SettingsWindow.
    /// Returns the SettingsWindow.
    /// </summary>
    private FlaUI.Core.AutomationElements.Window OpenSettingsWindow()
    {
        // Signal only the fixture-owned application
        _fx.RequestSettings();

        // Wait for PasswordPromptWindow
        FlaUI.Core.AutomationElements.Window prompt;
        try { prompt = _fx.App.WaitForWindow(_fx.Automation, "Protected Access"); }
        catch
        {
            output.WriteLine($"Fixture exited={_fx.App.HasExited}; windows=" + string.Join(" | ",
                _fx.App.GetAllTopLevelWindows(_fx.Automation).Select(w => $"{w.Title} enabled={w.IsEnabled}")));
            foreach (var log in Directory.EnumerateFiles(_fx.Runtime.Paths.Root, "*.log", SearchOption.AllDirectories))
                output.WriteLine(File.ReadAllText(log));
            throw;
        }

        var passwordBoxes = prompt.FindAllDescendants(cf =>
            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
        if (passwordBoxes.Length > 0)
        {
            passwordBoxes[0].Click();
            Keyboard.Type(AppFixture.TestPassword);
        }

        prompt.FindButton("Unlock").Click();

        return _fx.App.WaitForWindow(_fx.Automation, "ScreenTime Settings");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_OpensAfterCorrectPassword()
    {
        var settings = OpenSettingsWindow();
        Assert.Contains("Settings", settings.Title);
        settings.Close();
    }

    [Fact]
    public void Settings_AddRuleButton_IsPresent()
    {
        var settings = OpenSettingsWindow();
        var addBtn = settings.FindButton("➕ Add Rule");
        Assert.NotNull(addBtn);
        settings.Close();
    }

    [Fact]
    public void Settings_SaveButton_IsPresent()
    {
        var settings = OpenSettingsWindow();
        var saveBtn = settings.FindButton("Save");
        Assert.NotNull(saveBtn);
        settings.Close();
    }

    [Fact]
    public void Settings_CancelButton_ClosesWindow()
    {
        var settings = OpenSettingsWindow();
        settings.FindButton("Cancel").Click();
        Thread.Sleep(500);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Settings") == true),
            "SettingsWindow should close on Cancel.");
    }

    [Fact]
    public void Settings_DashboardButton_OpensDashboard()
    {
        var settings = OpenSettingsWindow();
        settings.FindButton("📊 Dashboard").Click();

        var dashboard = _fx.App.WaitForWindow(_fx.Automation, "Usage Dashboard");
        Assert.Contains("Dashboard", dashboard.Title);

        Assert.True(dashboard.IsEnabled);
        Assert.DoesNotContain(_fx.App.GetAllTopLevelWindows(_fx.Automation), w => w.Title == "ScreenTime Settings");
        dashboard.Close();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_DashboardActivatesExistingSingleton_AfterModalCloses(bool tray)
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
        using (var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName)) signal.Set();
        var existing = _fx.App.WaitForWindow(_fx.Automation, "Usage Dashboard");
        var hwnd = existing.Properties.NativeWindowHandle.Value;
        var settings = OpenSettingsWindow();
        if (tray) { using var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName); signal.Set(); }
        else settings.FindButton("📊 Dashboard").Invoke();
        var dashboard = _fx.App.WaitForWindow(_fx.Automation, "Usage Dashboard");
        Assert.True(SpinWait.SpinUntil(() => dashboard.IsEnabled, TimeSpan.FromSeconds(3)));
        Assert.Equal(hwnd, dashboard.Properties.NativeWindowHandle.Value);
        Assert.Single(_fx.App.GetAllTopLevelWindows(_fx.Automation).Where(w => w.Title.Contains("Usage Dashboard")));
        dashboard.Close();
        Assert.False(_fx.App.HasExited);
        // The same process must accept a fresh protected command after modal navigation.
        _fx.OpenDatabase().SetSetting("OverallDailyLimitMinutes", "123");
        var reopened = OpenSettingsWindow();
        reopened.Close();
        }
    }

    [Fact]
    public void DeferredCapAndBreakControlsAbsent_SavePreservesStoredLegacyCap()
    {
        _fx.OpenDatabase().SetSetting("OverallDailyLimitMinutes", "123");
        var settings = OpenSettingsWindow();
        Assert.Null(settings.FindTextContaining("Daily Cap"));
        Assert.Null(settings.FindTextContaining("Break every"));
        Assert.Null(settings.FindTextContaining("Break dur."));
        Assert.Null(settings.FindTextContaining("[test]"));
        Assert.NotNull(settings.FindTextContaining("No recent applications"));
        settings.FindButton("Save").Invoke();
        Assert.Equal("123", _fx.OpenDatabase().GetSetting("OverallDailyLimitMinutes"));
    }

    [Theory]
    [InlineData("picker")]
    [InlineData("editor")]
    [InlineData("confirmation")]
    public void Dashboard_CancelsNestedProtectedFlow_ThenAllowsFreshPickerAndEnforcement(string nested)
    {
        var db = _fx.OpenDatabase();
        db.SaveRule(new() { ProcessName = "nested-navigation", DisplayName = "Nested navigation", DailyLimitMinutes = 60 });
        using (var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName)) signal.Set();
        var dashboard = _fx.App.WaitForWindow(_fx.Automation, "Usage Dashboard");
        var settings = OpenSettingsWindow();
        var before = System.Text.Json.JsonSerializer.Serialize(db.LoadConfig().Rules);
        string title;
        if (nested == "picker") { settings.FindButton("🔍 Pick Process").Invoke(); title = "Pick a Running Process"; }
        else if (nested == "editor") { settings.FindButton("➕ Add Rule").Invoke(); title = "Edit App Rule"; }
        else
        {
            settings.FindFirstDescendant(cf => cf.ByAutomationId("RulesGrid"))
                .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
                .Single(row => row.FindTextContaining("Nested navigation") is not null).Patterns.SelectionItem.Pattern.Select();
            settings.FindButton("🗑️ Delete").Invoke(); title = "Confirm";
        }
        _fx.App.WaitForWindow(_fx.Automation, title);
        using (var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName)) signal.Set();
        Assert.True(SpinWait.SpinUntil(() => dashboard.IsEnabled &&
            _fx.App.GetAllTopLevelWindows(_fx.Automation).All(w => w.Title.Contains("Usage Dashboard")), TimeSpan.FromSeconds(5)),
            "Dashboard navigation must unwind all protected modal windows.");
        Assert.False(_fx.App.HasExited);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(db.LoadConfig().Rules));

        // A fresh picker must still select an owned helper and cancel normally after navigation.
        var helper = _fx.LaunchHelper(headless: true);
        settings = OpenSettingsWindow();
        settings.FindButton("🔍 Pick Process").Invoke();
        var picker = _fx.App.WaitForWindow(_fx.Automation, "Pick a Running Process");
        picker.FindTextBox("SearchBox").AsTextBox().Text = "screentime.testprocess";
        var list = picker.FindFirstDescendant(cf => cf.ByAutomationId("ProcessList")).AsListBox();
        Assert.True(SpinWait.SpinUntil(() => list.Items.Length == 1, TimeSpan.FromSeconds(3)));
        list.Items[0].Select(); picker.FindButton("Select").Invoke();
        _fx.App.WaitForWindow(_fx.Automation, "Edit App Rule").FindButton("Cancel").Invoke();
        Assert.True(SpinWait.SpinUntil(() => settings.IsEnabled, TimeSpan.FromSeconds(3)));
        settings.FindButton("🔍 Pick Process").Invoke();
        _fx.App.WaitForWindow(_fx.Automation, "Pick a Running Process").FindButton("Cancel").Invoke();
        Assert.True(SpinWait.SpinUntil(() => settings.IsEnabled, TimeSpan.FromSeconds(3)));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(db.LoadConfig().Rules));
        // Stop this exact owned helper before adding current downtime, then verify a new instance is denied.
        using (var owned = Process.GetProcessById(helper.Id))
        {
            _ = owned.Handle;
            Assert.True(helper.Matches(owned)); owned.Kill(); Assert.True(owned.WaitForExit(3000));
        }
        db.SaveRule(new() { ProcessName = "screentime.testprocess", DisplayName = "Owned enforcement helper", DailyLimitMinutes = 1,
            BlockedPeriods = [new() { StartDayOfWeek = DateTime.Today.DayOfWeek, StartMinute = 0, EndMinute = 0, EndDayOffset = 1 }] });
        settings.Close(); dashboard.Close();
        Assert.True(SpinWait.SpinUntil(() => _fx.App.GetAllTopLevelWindows(_fx.Automation).Length == 0, TimeSpan.FromSeconds(3)));
        using (var signal = EventWaitHandle.OpenExisting(_fx.Runtime.StatusEventName)) signal.Set();
        Window? status = null;
        var observed = SpinWait.SpinUntil(() =>
        {
            status = _fx.App.GetAllTopLevelWindows(_fx.Automation).SingleOrDefault(w => w.Title == "ScreenTime");
            return status?.FindTextContaining("Owned enforcement helper") is not null && status.FindTextContaining("DOWNTIME") is not null;
        }, TimeSpan.FromSeconds(5));
        output.WriteLine("Enforcement status: " + string.Join(" | ", status?.FindAllDescendants().Select(e => e.Name) ?? []));
        Assert.True(observed);
        status!.Close();
        var denied = _fx.LaunchHelper(headless: true);
        using var process = Process.GetProcessById(denied.Id);
        Assert.True(process.WaitForExit(8000)); Assert.False(_fx.App.HasExited);
    }
}
