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

        return _fx.App.WaitForWindow(_fx.Automation, "ScreenTime — Main");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_OpensAfterCorrectPassword()
    {
        var settings = OpenSettingsWindow();
        Assert.Equal("ScreenTime — Main", settings.Title);
        Assert.NotNull(settings.FindButton("Settings 🔓"));
        Assert.NotNull(settings.FindButton("➕ Add Rule"));
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
    public void Settings_CancelButton_ReturnsToTodayInSameWindow()
    {
        var settings = OpenSettingsWindow();
        settings.FindButton("Cancel").Click();
        Thread.Sleep(500);

        Assert.NotNull(settings.FindTextContaining("Last 7 Days"));
        Assert.Equal(settings.Properties.NativeWindowHandle.Value,
            Assert.Single(_fx.App.GetAllTopLevelWindows(_fx.Automation).Where(w => w.Title == "ScreenTime — Main"))
                .Properties.NativeWindowHandle.Value);
    }

    [Fact]
    public void Settings_TodayButton_NavigatesWithinSameWindow()
    {
        var settings = OpenSettingsWindow();
        var handle = settings.Properties.NativeWindowHandle.Value;
        settings.FindButton("Today").Click();

        var dashboard = _fx.App.WaitForWindow(_fx.Automation, "ScreenTime — Main");
        Assert.Equal(handle, dashboard.Properties.NativeWindowHandle.Value);

        Assert.True(dashboard.IsEnabled);
        Assert.Single(_fx.App.GetAllTopLevelWindows(_fx.Automation).Where(w => w.Title == "ScreenTime — Main"));
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
        var existing = _fx.App.WaitForWindow(_fx.Automation, "ScreenTime — Main");
        var hwnd = existing.Properties.NativeWindowHandle.Value;
        var bounds = existing.BoundingRectangle;
        var settings = OpenSettingsWindow();
        Assert.Equal(bounds, settings.BoundingRectangle);
        if (tray) { using var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName); signal.Set(); }
        else settings.FindButton("Today").Invoke();
        var dashboard = _fx.App.WaitForWindow(_fx.Automation, "ScreenTime — Main");
        Assert.True(SpinWait.SpinUntil(() => dashboard.IsEnabled, TimeSpan.FromSeconds(3)));
        Assert.Equal(hwnd, dashboard.Properties.NativeWindowHandle.Value);
        Assert.Equal(bounds, dashboard.BoundingRectangle);
        Assert.Single(_fx.App.GetAllTopLevelWindows(_fx.Automation).Where(w => w.Title.Contains("ScreenTime — Main")));
        dashboard.FindButton("Settings 🔓").Invoke();
        Assert.NotNull(dashboard.FindButton("➕ Add Rule"));
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

    [Fact]
    public void UnsavedNotificationChanges_UseSaveDiscardCancelBeforeTodayOrClose()
    {
        var settings = OpenSettingsWindow();
        var handle = settings.Properties.NativeWindowHandle.Value;
        settings.FindFirstDescendant(cf => cf.ByName("Notifications").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem))).AsTabItem().Select();
        settings.FindFirstDescendant(cf => cf.ByAutomationId("PresetBox")).AsComboBox().Select("Minimal");
        settings.FindButton("Today").Invoke();
        var confirmation = _fx.App.WaitForWindow(_fx.Automation, "Unsaved Settings");
        confirmation.FindButton("Cancel").Invoke();
        Assert.NotNull(settings.FindButton("Save"));
        settings.FindButton("Today").Invoke();
        confirmation = _fx.App.WaitForWindow(_fx.Automation, "Unsaved Settings");
        confirmation.FindButton("No").Invoke();
        Assert.NotNull(settings.FindTextContaining("Last 7 Days"));
        Assert.Equal(handle, settings.Properties.NativeWindowHandle.Value);
        Assert.False(_fx.App.HasExited);
        settings.FindButton("Settings 🔓").Invoke();
        settings.FindFirstDescendant(cf => cf.ByName("App Rules").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem))).AsTabItem().Select();
        Assert.NotNull(settings.FindButton("➕ Add Rule"));
        settings.Close();
        _fx.RequestSettings();
        var prompt = _fx.App.WaitForWindow(_fx.Automation, "Protected Access");
        prompt.Close();
    }

    [Fact]
    public void NestedRuleEditor_RemainsModalUntilExplicitlyCancelled()
    {
        var settings = OpenSettingsWindow();
        settings.FindButton("➕ Add Rule").Invoke();
        var editor = _fx.App.WaitForWindow(_fx.Automation, "Edit App Rule");
        using (var signal = EventWaitHandle.OpenExisting(_fx.Runtime.DashboardEventName)) signal.Set();
        Assert.NotNull(_fx.App.WaitForWindow(_fx.Automation, "Edit App Rule"));
        editor.FindButton("Cancel").Invoke();
        Assert.True(SpinWait.SpinUntil(() => settings.IsEnabled, TimeSpan.FromSeconds(3)));
        settings.FindButton("Today").Invoke();
        Assert.NotNull(settings.FindTextContaining("Last 7 Days"));
        settings.Close();
        Assert.False(_fx.App.HasExited);
    }
}
