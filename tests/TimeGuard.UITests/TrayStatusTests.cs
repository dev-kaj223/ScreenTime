using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

public class TrayStatusTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class StatusFixture : SeededAppFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            var db = OpenDatabase();
            db.SaveRule(new() { ProcessName = "screentime.testprocess", DisplayName = "Owned helper", DailyLimitMinutes = 1 });
            db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Now), new() { ProcessName = "screentime.testprocess", QuotaSeconds = 60, ObservedSeconds = 60 });
            db.SaveRule(new() { ProcessName = "example", DisplayName = "Example available", DailyLimitMinutes = 60 });
        }
    }
    private sealed class ManyAppFixture : SeededAppFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            for (var i = 0; i < 10; i++) OpenDatabase().SaveRule(new() { ProcessName = "example" + i, DisplayName = "Example application " + i, DailyLimitMinutes = 60 });
        }
    }
    private static void Signal(string name) { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
    private static void EnterPassword(FlaUI.Core.AutomationElements.Window prompt, string value)
    {
        prompt.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).Click();
        Keyboard.Type(value); prompt.FindButton("Unlock").Invoke();
    }

    [Fact]
    public void ReadOnlyCollection_NoPassword_Singleton_CloseLeavesEnforcementRunning()
    {
        using var fx = new StatusFixture();
        Signal(fx.Runtime.StatusEventName);
        Window? panel = null;
        var ready = SpinWait.SpinUntil(() =>
        {
            if (fx.App.HasExited) return false;
            panel = fx.App.GetAllTopLevelWindows(fx.Automation).SingleOrDefault(w => w.Title == "ScreenTime");
            var content = panel?.FindAllDescendants() ?? [];
            return content.Any(e => e.Name == "Owned helper") && content.Any(e => e.Name == "Example available");
        }, TimeSpan.FromSeconds(5));
        output.WriteLine($"Content ready={ready}; exited={fx.App.HasExited}; fixture windows=" +
            string.Join(" | ", fx.App.GetAllTopLevelWindows(fx.Automation).Select(w => w.Title)));
        Assert.True(ready, "Exact fixture popup must render both configured rows without being reopened.");
        Assert.Contains(panel!.FindAllDescendants(), e => e.Name == "Owned helper");
        Assert.Contains(panel.FindAllDescendants(), e => e.Name == "Example available");
        Assert.DoesNotContain(fx.App.GetAllTopLevelWindows(fx.Automation), w => w.Title == "Protected Access");
        AssertFullyOnscreen(panel);
        panel.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-status.png"));
        Signal(fx.Runtime.StatusEventName); Thread.Sleep(300);
        Assert.Empty(fx.App.GetAllTopLevelWindows(fx.Automation).Where(w => w.Title == "ScreenTime"));

        var helper = fx.LaunchHelper(headless: true);
        using var process = Process.GetProcessById(helper.Id);
        Assert.True(process.WaitForExit(8000));
        Assert.False(fx.App.HasExited);
        Assert.Equal(60, fx.OpenDatabase().LoadLog(DateOnly.FromDateTime(DateTime.Now)).GetOrCreate("screentime.testprocess").QuotaSeconds);
    }

    [Fact]
    public void ManyApps_DirectDetailsAndScrollSurviveRefresh_AndStayOnscreen()
    {
        using var fx = new ManyAppFixture();
        Signal(fx.Runtime.StatusEventName);
        var panel = fx.App.WaitForWindow(fx.Automation, "ScreenTime");
        Assert.DoesNotContain(panel.FindAllDescendants(), e => e.Name == "Details");
        var scroll = panel.FindFirstDescendant(cf => cf.ByAutomationId("AppListScroll")).Patterns.Scroll.Pattern;
        Assert.True(scroll.VerticallyScrollable.Value);
        scroll.SetScrollPercent(-1, 70);
        var position = scroll.VerticalScrollPercent.Value;
        Thread.Sleep(2200);

        Assert.InRange(scroll.VerticalScrollPercent.Value, position - 1, position + 1);
        AssertFullyOnscreen(panel);
        panel.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "phase6-many-apps.png"));
        panel.Close();
    }

    [Fact]
    public void WrongAndCancelledSettingsAndExit_DoNotStopOrMutate_CorrectExitStops()
    {
        using var fx = new SeededAppFixture();
        var config = System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadConfig());
        foreach (var signal in new[] { fx.Runtime.SettingsEventName, fx.Runtime.ExitEventName })
        {
            Signal(signal); var prompt = fx.App.WaitForWindow(fx.Automation, "Protected Access");
            EnterPassword(prompt, "wrong-password");
            Assert.True(SpinWait.SpinUntil(() => prompt.FindAllDescendants().Any(e => e.Name == "Incorrect password."), TimeSpan.FromSeconds(2)));
            Assert.False(fx.App.HasExited);
            prompt.Close();
            Assert.Equal(config, System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadConfig()));
            if (signal == fx.Runtime.SettingsEventName)
            {
                var main = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
                Assert.NotNull(main.FindButton("Settings 🔒"));
                Assert.NotNull(main.FindTextContaining("Last 7 Days"));
            }
        }
        Signal(fx.Runtime.ExitEventName);
        EnterPassword(fx.App.WaitForWindow(fx.Automation, "Protected Access"), AppFixture.TestPassword);
        Assert.True(SpinWait.SpinUntil(() => fx.App.HasExited, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProtectedNotificationSettings_SavePreset_PreviewAndCancelAreIsolated()
    {
        using var fx = new SeededAppFixture();
        var before = System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadConfig());
        fx.RequestSettings(); EnterPassword(fx.App.WaitForWindow(fx.Automation, "Protected Access"), AppFixture.TestPassword);
        var settings = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
        settings.FindFirstDescendant(cf => cf.ByName("Notifications").And(cf.ByControlType(ControlType.TabItem))).AsTabItem().Select();
        settings.FindFirstDescendant(cf => cf.ByAutomationId("PresetBox")).AsComboBox().Select("Minimal");
        settings.FindButton("Preview").Invoke();
        var preview = fx.App.WaitForWindow(fx.Automation, "ScreenTime notice");
        Assert.Contains(preview.FindAllDescendants(), e => e.Name.Contains("Example App"));
        Assert.Empty(fx.OpenDatabase().LoadGraceEpisodes());
        settings.FindButton("Save").Invoke();
        Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(fx.Runtime.Paths.Root, "notification-preferences.json")), TimeSpan.FromSeconds(2)));
        var store = new NotificationPreferenceStore(fx.Runtime.Paths);
        Assert.Equal(NotificationPreferences.Minimal, store.Load());
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadConfig()));
        settings.FindButton("Settings 🔓").Invoke();
        Assert.DoesNotContain(fx.App.GetAllTopLevelWindows(fx.Automation), w => w.Title == "Protected Access");
        settings.FindFirstDescendant(cf => cf.ByName("Notifications").And(cf.ByControlType(ControlType.TabItem))).AsTabItem().Select();
        settings.FindButton("Reset to defaults").Invoke();
        settings.FindButton("Cancel").Invoke();
        Assert.Equal(NotificationPreferences.Minimal, store.Load());
    }

    internal static void AssertFullyOnscreen(Window panel)
    {
        var hwnd = panel.Properties.NativeWindowHandle.Value;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out var bounds)) return false;
            return bounds.Left >= info.Work.Left && bounds.Top >= info.Work.Top && bounds.Right <= info.Work.Right && bounds.Bottom <= info.Work.Bottom;
        }, TimeSpan.FromSeconds(3)), "The complete status HWND must remain within its monitor work area after layout growth.");
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect bounds);
}
