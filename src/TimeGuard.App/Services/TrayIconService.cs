using System.Windows.Threading;
using TimeGuard.Models;
using TimeGuard.UI;
using TimeGuard.ViewModels;
using Forms = System.Windows.Forms;

namespace TimeGuard.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<StatusSnapshot?> _read;
    private readonly StatusViewModel _model = new();
    private StatusPanel? _panel;
    private readonly Action _dashboard;
    private readonly Action _settings;
    private readonly Func<Task> _exit;
    private System.Drawing.Point _dismissedAtCursor;
    private long _rightDismissedAt;
    private Forms.MouseButtons _pressedButton;
    private long _lastRightUp;
    private System.Drawing.Point _lastRightCursor;
    private bool _disposed;
    internal StatusPanel? Panel => _panel;
    internal bool IsDisposed => _disposed;

    public TrayIconService(Func<StatusSnapshot?> read, Action settings, Func<Task> exit, System.Windows.Media.ImageSource? branding = null, Action? dashboard = null)
    {
        _read = read;
        _settings = settings; _exit = exit; _dashboard = dashboard ?? (() => { });
        _image = Branding.ScreenTimeBranding.CreateTrayIcon(branding ?? Branding.ScreenTimeBranding.Image);
        // NotifyIcon owns the hidden native window and handles TaskbarCreated (Explorer recreation).
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "ScreenTime — Starting", Visible = true };
        // A NotifyIcon MouseClick may be synthesized around shell focus changes.
        // Dispatch only a complete same-button down/up gesture; in particular a
        // right-click popup activation cannot turn a stray left MouseClick into
        // a main-window request.
        _icon.MouseDown += (_, e) => HandleMouseDown(e.Button);
        _icon.MouseUp += (_, e) => HandleMouseUp(e.Button);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    internal void HandleMouseDown(Forms.MouseButtons button)
    {
        if (_disposed) return;
        _pressedButton = button is Forms.MouseButtons.Left or Forms.MouseButtons.Right ? button : Forms.MouseButtons.None;
    }

    internal void HandleMouseUp(Forms.MouseButtons button)
    {
        if (_disposed || _pressedButton != button) return;
        _pressedButton = Forms.MouseButtons.None;
        var cursor = Forms.Cursor.Position;
        if (button == Forms.MouseButtons.Left && _lastRightUp != 0 &&
            Environment.TickCount64 - _lastRightUp < 300 &&
            Math.Abs(cursor.X - _lastRightCursor.X) <= 4 && Math.Abs(cursor.Y - _lastRightCursor.Y) <= 4)
            return;
        if (button == Forms.MouseButtons.Right) { _lastRightUp = Environment.TickCount64; _lastRightCursor = cursor; }
        HandleClick(button);
    }

    internal void HandleClick(Forms.MouseButtons button)
    {
        if (_disposed) return;
        if (button == Forms.MouseButtons.Left) { _panel?.Close(); _rightDismissedAt = 0; _dashboard(); }
        else if (button == Forms.MouseButtons.Right)
        {
            // Explorer may deactivate the popup on button-down before NotifyIcon receives
            // button-up. Consume that same physical right click instead of reopening it.
            var cursor = Forms.Cursor.Position;
            var sameClick = _rightDismissedAt != 0 && Environment.TickCount64 - _rightDismissedAt < 1000 &&
                Math.Abs(cursor.X - _dismissedAtCursor.X) <= 4 && Math.Abs(cursor.Y - _dismissedAtCursor.Y) <= 4;
            _rightDismissedAt = 0;
            if (!sameClick) OpenStatus();
        }
    }

    public void OpenStatus()
    {
        if (_disposed) return;
        Refresh();
        if (_panel is not null) { _panel.Close(); return; }
        var panel = new StatusPanel { DataContext = _model };
        _panel = panel;
        panel.DashboardRequested += _dashboard;
        panel.SettingsRequested += _settings;
        panel.ExitRequested += async () => await _exit();
        panel.Deactivated += (_, _) =>
        {
            // Shell deactivation can be dispatched after the physical button is released.
            if (panel.DismissedByDeactivation)
            { _dismissedAtCursor = Forms.Cursor.Position; _rightDismissedAt = Environment.TickCount64; }
        };
        _timer.Interval = TimeSpan.FromSeconds(1);
        panel.Closed += (_, _) => { if (_panel == panel) _panel = null; _timer.Interval = TimeSpan.FromSeconds(5); };
        panel.Show();
        NativeSetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(panel).Handle);
        panel.Activate();
    }

    private void Refresh()
    {
        // Pure copied facts only: never poll processes, evaluate policy, or read storage here.
        _model.Refresh(_read(), DateTimeOffset.UtcNow);
        if (_icon.Text != _model.Tooltip) _icon.Text = _model.Tooltip;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _panel?.Close(); _panel = null;
        _icon.Visible = false; _icon.Dispose(); _image.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool NativeSetForegroundWindow(IntPtr hwnd);
}
