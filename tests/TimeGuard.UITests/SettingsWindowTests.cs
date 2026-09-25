using FlaUI.Core.Input;
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
}
