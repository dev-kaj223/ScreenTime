using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

public class Beta2PopupTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Fixture : SeededAppFixture
    {
        internal string Label => "UX fixture " + Runtime.RunId[..8];
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            OpenDatabase().SaveRule(new() { ProcessName = "beta2-" + Runtime.RunId, DisplayName = Label, DailyLimitMinutes = 60 });
        }
    }
    private static Window Popup(AppFixture fx)
    {
        Window? popup = null;
        Assert.True(SpinWait.SpinUntil(() => (popup = fx.App.GetAllTopLevelWindows(fx.Automation).SingleOrDefault(w => w.Title == "ScreenTime")) is not null, TimeSpan.FromSeconds(5)));
        return popup!;
    }
    private static void Signal(string name) { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
    private static void ClickOwned(AppFixture fx, Window expected, AutomationElement element)
    {
        var bounds = element.BoundingRectangle;
        var point = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        GetWindowThreadProcessId(WindowFromPoint(point), out var processId);
        Assert.Equal((uint)fx.App.ProcessId, processId);
        Assert.Equal(expected.Properties.NativeWindowHandle.Value, GetAncestor(WindowFromPoint(point), 2)); // Never click an obscured fixture button.
        Mouse.Click(point);
    }
    private static void Gone(AppFixture fx)
    {
        Assert.True(SpinWait.SpinUntil(() => !fx.App.GetAllTopLevelWindows(fx.Automation).Any(w => w.Title == "ScreenTime"), TimeSpan.FromSeconds(3)));
        Assert.False(fx.App.HasExited, "Dismissing the popup must not exit its process.");
    }

    [Fact]
    public void InteractivePopup_NativeFocusEscapeClickAway_FooterProtection_AndRepeatedLifecycle()
    {
        using var dpi = new PhysicalPixelScope();
        using var fx = new Fixture();
        Signal(fx.Runtime.DashboardEventName);
        var dashboard = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
        dashboard.SetForeground();
        Signal(fx.Runtime.StatusEventName);
        var popup = Popup(fx);
        var hwnd = popup.Properties.NativeWindowHandle.Value;
        Assert.True(SpinWait.SpinUntil(() => GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(3)));
        Assert.Equal(0L, GetWindowLongPtr(hwnd, -16).ToInt64() & 0x00C00000L); // No caption.
        Assert.Equal(0L, GetWindowLongPtr(hwnd, -20).ToInt64() & 0x00040000L); // No taskbar APPWINDOW.
        ClickOwned(fx, popup, popup.FindButton("Dashboard")); Gone(fx);
        Assert.Single(fx.App.GetAllTopLevelWindows(fx.Automation).Where(w => w.Title.Contains("ScreenTime — Main")));
        Signal(fx.Runtime.StatusEventName); popup = Popup(fx);
        ClickOwned(fx, popup, popup.FindButton("Settings")); Gone(fx);
        var prompt = fx.App.WaitForWindow(fx.Automation, "Protected Access");
        prompt.Close();
        Assert.False(fx.App.HasExited);
        Assert.NotNull(dashboard.FindButton("Settings 🔒"));
        Signal(fx.Runtime.StatusEventName); popup = Popup(fx);
        ClickOwned(fx, popup, popup.FindButton("Settings")); Gone(fx);
        prompt = fx.App.WaitForWindow(fx.Automation, "Protected Access");
        ClickOwned(fx, prompt, prompt.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")));
        Keyboard.Type(AppFixture.TestPassword); prompt.FindButton("Unlock").Invoke();
        Assert.NotNull(dashboard.FindButton("Settings 🔓"));
        Assert.NotNull(dashboard.FindButton("➕ Add Rule"));
        dashboard.FindButton("Today").Invoke();
        Signal(fx.Runtime.StatusEventName); popup = Popup(fx);
        ClickOwned(fx, popup, popup.FindButton("Exit")); Gone(fx);
        prompt = fx.App.WaitForWindow(fx.Automation, "Protected Access");
        ClickOwned(fx, prompt, prompt.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")));
        Keyboard.Type("incorrect"); prompt.FindButton("Unlock").Invoke();
        Assert.True(SpinWait.SpinUntil(() => prompt.FindTextContaining("Incorrect password") is not null, TimeSpan.FromSeconds(3)));
        prompt.Close(); Assert.False(fx.App.HasExited);
        for (var i = 0; i < 3; i++)
        {
            Signal(fx.Runtime.StatusEventName); popup = Popup(fx);
            Assert.True(SpinWait.SpinUntil(() => GetForegroundWindow() == popup.Properties.NativeWindowHandle.Value, TimeSpan.FromSeconds(3)));
            Keyboard.Press(VirtualKeyShort.ESCAPE); Keyboard.Release(VirtualKeyShort.ESCAPE); Gone(fx);
            Signal(fx.Runtime.StatusEventName); popup = Popup(fx);
            // A real click in the fixture's existing Dashboard changes activation.
            var bounds = dashboard.BoundingRectangle;
            var candidates = new[] { (0.2, 0.2), (0.8, 0.2), (0.2, 0.8), (0.8, 0.8) }
                .Select(p => new System.Drawing.Point((int)(bounds.Left + p.Item1 * bounds.Width), (int)(bounds.Top + p.Item2 * bounds.Height))).ToArray();
            var target = candidates.FirstOrDefault(p => GetAncestor(WindowFromPoint(p), 2) == dashboard.Properties.NativeWindowHandle.Value);
            Assert.Contains(target, candidates);
            Assert.Equal(dashboard.Properties.NativeWindowHandle.Value, GetAncestor(WindowFromPoint(target), 2));
            Mouse.Click(target); Gone(fx);
        }
        dashboard.Close(); Assert.False(fx.App.HasExited);
    }

    [Fact]
    public void RealTrayLeftOpensSingleton_RightTogglesSamePhysicalClick()
    {
        using var dpi = new PhysicalPixelScope();
        using var fx = new Fixture();
        var desktop = fx.Automation.GetDesktop();
        AutomationElement[] Roots() => desktop.FindAllChildren().Where(e => e.Properties.ClassName.ValueOrDefault is
            "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland").ToArray();
        AutomationElement? Icon() => Roots().SelectMany(e => e.FindAllDescendants()).Where(e =>
            e.Properties.IsOffscreen.TryGetValue(out var offscreen) && !offscreen && (e.Properties.Name.ValueOrDefault ?? "").Trim().EndsWith($"ScreenTime — {fx.Label}: 60 min remaining", StringComparison.Ordinal)).SingleOrDefault();
        FlaUI.Core.AutomationElements.Button? overflow = null;
        AutomationElement Find()
        {
            AutomationElement? icon = null;
            if (!SpinWait.SpinUntil(() => (icon = Icon()) is not null, TimeSpan.FromSeconds(6)))
            {
                output.WriteLine("Fixture shell matches: " + string.Join(" | ", Roots().SelectMany(e => e.FindAllDescendants()).Select(e => e.Properties.Name.ValueOrDefault).Where(n => n is not null && (n.Contains(fx.Label) || n.Contains("hidden", StringComparison.OrdinalIgnoreCase) || n.Contains("chevron", StringComparison.OrdinalIgnoreCase)))));
                overflow = Roots().SelectMany(e => e.FindAllDescendants()).Single(e => e.Properties.IsOffscreen.TryGetValue(out var offscreen) && !offscreen &&
                    new[] { "Show hidden icons", "Hidden icon menu", "Notification Chevron" }.Contains((e.Properties.Name.ValueOrDefault ?? "").Trim(), StringComparer.OrdinalIgnoreCase)).AsButton();
                overflow.Invoke();
                var found = SpinWait.SpinUntil(() => (icon = Icon()) is not null, TimeSpan.FromSeconds(3));
                if (!found) output.WriteLine("Shell roots=" + string.Join(",", desktop.FindAllChildren().Select(e => e.Properties.ClassName.ValueOrDefault)) + "; owned labels=" + string.Join("|", desktop.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault).Where(n => n is not null && n.Contains(fx.Label))));
                Assert.True(found, "Unique fixture tray identity was not visible.");
            }
            var bounds = icon!.BoundingRectangle;
            var hit = fx.Automation.FromPoint(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            var walker = fx.Automation.TreeWalkerFactory.GetRawViewWalker();
            var ownedHit = false;
            for (var depth = 0; hit is not null && depth < 8; depth++)
            {
                if (fx.Automation.Compare(hit, icon)) { ownedHit = true; break; }
                if (Roots().Any(root => fx.Automation.Compare(root, hit))) break;
                hit = walker.GetParent(hit);
            }
            Assert.True(ownedHit, "The physical click must hit the uniquely identified fixture icon or its child.");
            return icon;
        }
        try
        {
            Find().RightClick(); var popup = Popup(fx);
            Assert.Empty(fx.App.GetAllTopLevelWindows(fx.Automation).Where(w => w.Title == "ScreenTime — Main"));
            Find().RightClick(); Gone(fx);
            Find().Click();
            var dashboard = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
            Find().Click(); Thread.Sleep(250);
            Assert.Single(fx.App.GetAllTopLevelWindows(fx.Automation).Where(w => w.Title.Contains("ScreenTime — Main")));
            Find().RightClick(); popup = Popup(fx);
            Assert.Equal(popup.Properties.NativeWindowHandle.Value, GetForegroundWindow());
            // Keep the same actual icon coordinate, including when its overflow flyout closes.
            Find().RightClick(); Gone(fx);
            Find().RightClick(); popup = Popup(fx);
            // Escape while the pointer still rests on the tray must allow the next right click.
            Keyboard.Press(VirtualKeyShort.ESCAPE); Keyboard.Release(VirtualKeyShort.ESCAPE); Gone(fx);
            Find().RightClick(); popup = Popup(fx);
            Assert.Contains(popup.FindAllDescendants(), e => (e.Properties.Name.ValueOrDefault ?? "").Trim() == fx.Label);
            popup.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-native-popup.png"));
            ClickOwned(fx, popup, popup.FindButton("Dashboard")); Gone(fx); dashboard.Close();
        }
        finally
        {
            if (overflow is not null && Roots().Any(e => e.Properties.IsOffscreen.TryGetValue(out var offscreen) && !offscreen && e.Properties.ClassName.ValueOrDefault is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) overflow.Invoke();
        }
    }
    [Fact]
    public void Popup_AllAvailableMonitorCorners_StayWithinWorkArea()
    {
        using var dpi = new PhysicalPixelScope();
        using var fx = new Fixture();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            foreach (var point in new[] { new System.Drawing.Point(area.Left + 2, area.Top + 2), new System.Drawing.Point(area.Right - 2, area.Bottom - 2) })
            {
                Mouse.MoveTo(point);
                Signal(fx.Runtime.StatusEventName);
                var popup = Popup(fx);
                TrayStatusTests.AssertFullyOnscreen(popup);
                Assert.True(SpinWait.SpinUntil(() => System.Windows.Forms.Screen.FromHandle(popup.Properties.NativeWindowHandle.Value).DeviceName == screen.DeviceName, TimeSpan.FromSeconds(3)));
                Signal(fx.Runtime.StatusEventName); Gone(fx);
            }
        }
        output.WriteLine("Actual monitor work areas checked: " + System.Windows.Forms.Screen.AllScreens.Length);
    }

    private sealed class PhysicalPixelScope : IDisposable
    {
        private readonly IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
