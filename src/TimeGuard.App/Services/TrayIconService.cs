using System.Windows.Threading;
using TimeGuard.Models;
using TimeGuard.UI;
using TimeGuard.ViewModels;
using Forms = System.Windows.Forms;

namespace TimeGuard.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly System.Drawing.Icon _image;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<StatusSnapshot?> _read;
    private readonly StatusViewModel _model = new();
    private StatusPanel? _panel;
    private bool _disposed;
    internal StatusPanel? Panel => _panel;
    internal bool IsDisposed => _disposed;

    public TrayIconService(Func<StatusSnapshot?> read, Action settings, Func<Task> exit, System.Windows.Media.ImageSource? branding = null)
    {
        _read = read;
        _image = Branding.ScreenTimeBranding.CreateTrayIcon(branding ?? Branding.ScreenTimeBranding.Image);
        _menu.Items.Add("Open ScreenTime", null, (_, _) => OpenStatus());
        _menu.Items.Add("Settings", null, (_, _) => settings());
        _menu.Items.Add("Exit ScreenTime", null, async (_, _) => await exit());
        // NotifyIcon owns the hidden native window and handles TaskbarCreated (Explorer recreation).
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "ScreenTime — Starting", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenStatus(); };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void OpenStatus()
    {
        if (_disposed) return;
        Refresh();
        if (_panel is not null) { _panel.Activate(); return; } // Explicit user command only.
        var panel = new StatusPanel { DataContext = _model };
        _panel = panel;
        _timer.Interval = TimeSpan.FromSeconds(1);
        panel.Closed += (_, _) => { if (_panel == panel) _panel = null; _timer.Interval = TimeSpan.FromSeconds(5); };
        panel.Show();
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
        _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose();
    }
}
