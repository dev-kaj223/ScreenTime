using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TimeGuard.Helpers;
using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.UI;
using TimeGuard.ViewModels;
using Xunit;

namespace TimeGuard.UITests;

public class TrayPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static AppStatus Status(string name, int minutes = 60, long used = 0, bool running = false, bool down = false, bool grace = false)
    {
        var facts = new PolicySnapshot(name, name, true, new(2026, 9, 25), minutes, used, false, running,
            new(down, down ? Now.AddHours(5) : null, Now.AddDays(1)), down ? Now.AddHours(5) : Now);
        var decision = new RulesEngine().Evaluate(facts);
        if (grace) decision = decision with { State = PolicyState.QuotaExhaustedGrace, MayLaunch = false,
            Grace = new("episode-" + name, name, facts.Date, Now.AddMinutes(-10), Now.AddMinutes(10), GracePhase.Active, null, Array.Empty<ProcessInstance>()) };
        return new(facts, decision, used, 0);
    }

    [Fact]
    public void SingleAndManyApps_SortIndependentStates_TruthfulDowntimeGraceUnlimited_AndTooltip()
    {
        var model = new StatusViewModel();
        model.Refresh(new(Now, [Status("Apex Legends", used: 1080)]), Now);
        Assert.Equal("ScreenTime — Apex Legends: 42 min remaining", model.Tooltip);
        Assert.Contains("18 min used / 1 hr", model.Apps[0].Usage);
        Assert.Contains("42 min remaining", model.Apps[0].Remaining);
        var rowIdentity = model.Apps[0];
        model.Refresh(new(Now, [Status("Apex Legends", used: 1081)]), Now.AddSeconds(1));
        Assert.Same(rowIdentity, model.Apps[0]);
        var grace = Status("Grace", used: 3600, running: true, down: true, grace: true);
        model.Refresh(new(Now, [Status("Available"), Status("Running", running: true), Status("Blocked", down: true),
            grace, Status("A tie", used: 1200), Status("B tie", used: 1200)]), Now.AddSeconds(13));
        Assert.Equal(new[] { "Grace", "Blocked", "Running", "A tie", "B tie", "Available" }, model.Apps.Select(a => a.DisplayName));
        Assert.Equal("ScreenTime — 6 apps monitored · 2 restricted", model.Tooltip);
        Assert.Equal("9:47 remaining", model.Apps[0].GraceCountdown);
        Assert.Equal("FINISH SESSION", model.Apps[0].State);
        Assert.Equal("Downtime active", model.Apps[0].Downtime);
        Assert.Contains("New sessions are blocked", model.Apps[0].GraceDetail);
        Assert.Contains("unavailable", model.Apps[1].Remaining);
        Assert.Contains("New-session availability:", model.Apps[1].NextAvailability);
        model.Refresh(new(Now, [Status("Unlimited", minutes: 0)]), Now);
        Assert.Null(model.Apps[0].RemainingSeconds);
        Assert.Contains("Unlimited", model.Apps[0].Usage);
        model.Refresh(new(Now, [Status(new string('x', 300))]), Now);
        Assert.True(model.Tooltip.Length <= 127);
        model.Refresh(new(Now, []), Now);
        Assert.Empty(model.Apps); Assert.Contains("No apps", model.Tooltip);
        Assert.Equal(Now.AddMinutes(10), grace.Decision.Grace!.ExpiresAtUtc);
    }

    [Theory] [InlineData(null)] [InlineData("wrong")] [InlineData("correct")]
    public async Task ProtectedCommands_AuthenticateAtBoundary_CancelAndWrongAreNoops(string? entered)
    {
        var password = PasswordHelper.Hash("correct"); var settings = 0; var exits = 0; var prompts = 0;
        var access = new SettingsAccessService(() => password, () => { prompts++; return entered; },
            () => settings++, () => { exits++; return Task.CompletedTask; }, () => false);
        access.OpenSettings(); await access.ExitAsync();
        Assert.Equal(2, prompts);
        Assert.Equal(entered == "correct" ? 1 : 0, settings);
        Assert.Equal(entered == "correct" ? 1 : 0, exits);
    }

    [Fact]
    public async Task ProtectedCommands_StoppingAndReentrancyCannotBypassAuthentication()
    {
        var password = PasswordHelper.Hash("correct"); var actions = 0; var prompts = 0;
        SettingsAccessService? access = null;
        access = new(() => password, () => { prompts++; access!.OpenSettings(); return "correct"; },
            () => actions++, () => Task.CompletedTask, () => false);
        access.OpenSettings(); Assert.Equal(1, prompts); Assert.Equal(1, actions);
        var stopped = new SettingsAccessService(() => password, () => throw new Exception(), () => actions++, () => throw new Exception(), () => true);
        stopped.OpenSettings(); await stopped.ExitAsync(); Assert.Equal(1, actions);
    }

    [Fact]
    public void RightTrayGesture_RequestsPopupAndNeverRequestsMainWindow()
    {
        Sta(() =>
        {
            _ = new StatusPanel();
            var branding = new ResourceDictionary { Source = new Uri("/ScreenTime;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
            var dashboards = 0;
            using var tray = new TrayIconService(() => new(Now, []), () => { }, () => Task.CompletedTask,
                (ImageSource)branding["ScreenTimeBrandImage"], dashboard: () => dashboards++);
            tray.HandleMouseDown(System.Windows.Forms.MouseButtons.Right);
            tray.HandleMouseUp(System.Windows.Forms.MouseButtons.Right);
            Assert.NotNull(tray.Panel);
            Assert.Equal(0, dashboards);
            tray.HandleMouseUp(System.Windows.Forms.MouseButtons.Left); // shell-generated unmatched event
            Assert.Equal(0, dashboards);
            tray.HandleMouseDown(System.Windows.Forms.MouseButtons.Right);
            tray.HandleMouseUp(System.Windows.Forms.MouseButtons.Right);
            Assert.Null(tray.Panel);
            Assert.Equal(0, dashboards);
            // A genuine left gesture immediately after right must still open Today.
            tray.HandleMouseDown(System.Windows.Forms.MouseButtons.Left);
            tray.HandleMouseUp(System.Windows.Forms.MouseButtons.Left);
            tray.HandleMouseUp(System.Windows.Forms.MouseButtons.Left); // duplicate up is ignored
            Assert.Equal(1, dashboards);
        });
    }

    [Fact]
    public void BoundedReadOnlyLayout_SharedBranding_TraySingleton_CloseAndDispose()
    {
        Sta(() =>
        {
            var panel = new StatusPanel(); // Initialize WPF's pack-resource machinery before loading the dictionary.
            var branding = new ResourceDictionary { Source = new Uri("/ScreenTime;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
            var brand = (ImageSource)branding["ScreenTimeBrandImage"];
            using var icon = TimeGuard.Branding.ScreenTimeBranding.CreateTrayIcon(brand);
            Assert.Equal(32, icon.Width);
            var rows = Enumerable.Range(0, 12).Select(i => Status("App " + i)).ToArray();
            var model = new StatusViewModel(); model.Refresh(new(Now, rows), Now);
            panel.DataContext = model;
            panel.Show(); panel.UpdateLayout();
            var scroll = (ScrollViewer)panel.FindName("AppListScroll");
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.MaxHeight <= 520); Assert.True(panel.MaxHeight <= 650);
            Assert.Equal(12, ((ItemsControl)panel.FindName("AppList")).Items.Count);
            Assert.True(panel.ShowActivated); Assert.Equal(WindowStyle.None, panel.WindowStyle); Assert.False(panel.ShowInTaskbar);
            panel.Close();
            var protectedCalls = 0;
            var tray = new TrayIconService(() => new(Now, rows), () => protectedCalls++, () => { protectedCalls++; return Task.CompletedTask; }, brand);
            tray.OpenStatus(); var first = tray.Panel; Assert.NotNull(first);
            // Exercise the actual WinForms TaskbarCreated handler on this fixture's hidden HWND.
            // Do not restart Explorer or broadcast a message to other applications.
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var nativeIcon = (System.Windows.Forms.NotifyIcon)typeof(TrayIconService).GetField("_icon", flags)!.GetValue(tray)!;
            var fields = typeof(System.Windows.Forms.NotifyIcon).GetFields(flags);
            var nativeWindow = (System.Windows.Forms.NativeWindow)fields.Single(f => typeof(System.Windows.Forms.NativeWindow).IsAssignableFrom(f.FieldType)).GetValue(nativeIcon)!;
            Assert.NotEqual(IntPtr.Zero, nativeWindow.Handle);
            SendMessage(nativeWindow.Handle, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
            Assert.True(nativeIcon.Visible);
            Assert.True((bool)fields.Single(f => f.Name.Equals("_added", StringComparison.OrdinalIgnoreCase)).GetValue(nativeIcon)!);
            tray.OpenStatus(); Assert.Null(tray.Panel);
            Assert.False(tray.IsDisposed);
            tray.OpenStatus(); Assert.NotSame(first, tray.Panel);
            tray.Dispose(); tray.Dispose(); Assert.True(tray.IsDisposed); Assert.Null(tray.Panel);
            Assert.Equal(0, protectedCalls);
            var graceModel = new StatusViewModel();
            graceModel.Refresh(new(Now, [Status("Example finish session", used: 3600, running: true, down: true, grace: true)]), Now.AddSeconds(13));
            var gracePanel = new StatusPanel { DataContext = graceModel };
            gracePanel.Resources.MergedDictionaries.Add(branding);
            gracePanel.Show(); gracePanel.UpdateLayout();
            var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(gracePanel.ActualWidth),
                (int)Math.Ceiling(gracePanel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(gracePanel);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
            using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "phase6-grace.png"))) encoder.Save(stream);
            gracePanel.Close();
        });
    }

    [Fact]
    public void Preview_IsSyntheticAndUsesChosenCountdownAndColor_WithoutProfile()
    {
        var options = NotificationPreferences.Standard with { CountdownSeconds = 12, CriticalColor = "#FABCDE" };
        var request = NotificationPreviewService.Create(NotificationKind.GraceFinalMinute, options, Now);
        Assert.Equal("Example App", request.DisplayName); Assert.Equal("preview", request.AppKey);
        Assert.Equal(Now.AddSeconds(12), request.GraceDeadlineUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), NotificationPreviewService.Create(NotificationKind.QuotaTenMinutes, options, Now).Remaining);
        Sta(() =>
        {
            var window = new PassiveNoticeWindow(request, () => true, null, options);
            Assert.Equal("#FFFABCDE", ((SolidColorBrush)((Border)window.Content).BorderBrush).Color.ToString());
            Assert.False(window.ShowActivated); Assert.False(window.IsHitTestVisible);
            window.Close();
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Popup_FirstOpaqueFrame_IsAlreadyAtClampedAnchor(int appCount)
    {
        Sta(() =>
        {
            Assert.True(GetCursorPos(out var anchor));
            var monitor = new PlacementMonitor { Size = System.Runtime.InteropServices.Marshal.SizeOf<PlacementMonitor>() };
            Assert.True(GetMonitorInfo(MonitorFromPoint(anchor, 2), ref monitor));
            var model = new StatusViewModel();
            model.Refresh(new(Now, Enumerable.Range(0, appCount).Select(i => Status("App " + i)).ToArray()), Now);
            var panel = new StatusPanel { DataContext = model };
            Assert.True(panel.AllowsTransparency);
            Assert.Equal(0, panel.Opacity);
            var revealed = false;
            var opacity = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(StatusPanel));
            EventHandler onReveal = (_, _) =>
            {
                if (panel.Opacity == 0) return;
                Assert.True(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out var bounds));
                var area = monitor.Work;
                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                var x = anchor.X < area.Left + (area.Right - area.Left) / 2 ? anchor.X + 12 : anchor.X - width - 12;
                var y = anchor.Y < area.Top + (area.Bottom - area.Top) / 2 ? anchor.Y + 12 : anchor.Y - height - 12;
                Assert.Equal(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)), bounds.Left);
                Assert.Equal(Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)), bounds.Top);
                Assert.InRange(bounds.Right, area.Left, area.Right);
                Assert.InRange(bounds.Bottom, area.Top, area.Bottom);
                revealed = true;
            };
            opacity.AddValueChanged(panel, onReveal);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var deadline = DateTime.UtcNow.AddSeconds(5);
            timer.Tick += (_, _) => { if (revealed || DateTime.UtcNow >= deadline) frame.Continue = false; };
            try
            {
                panel.Show();
                Assert.Equal(0, panel.Opacity); // Show cannot expose the initial default location.
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.True(revealed, "Popup must become opaque only after successful native placement.");
            }
            finally { timer.Stop(); opacity.RemoveValueChanged(panel, onReveal); panel.Close(); }
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PlacementPoint { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PlacementRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PlacementMonitor { public int Size; public PlacementRect Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PlacementPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PlacementPoint point, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref PlacementMonitor info);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out PlacementRect bounds);

    internal static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The isolated STA presentation check timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
