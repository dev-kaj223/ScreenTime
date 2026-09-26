using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TimeGuard.UI;

public partial class StatusPanel : Window
{
    internal event Action? DashboardRequested;
    internal event Action? SettingsRequested;
    internal event Action? ExitRequested;
    private bool _placing;
    private bool _closing;
    internal bool DismissedByDeactivation { get; private set; }
    private bool _placementQueued;
    private readonly Point _anchor;
    public StatusPanel()
    {
        InitializeComponent();
        NativeGetCursorPos(out _anchor);
        Deactivated += (_, _) => { if (!_closing) { DismissedByDeactivation = true; Close(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        ContentRendered += (_, _) => QueuePlacement();
        SizeChanged += (_, _) => { if (IsLoaded) QueuePlacement(); };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true; // Closing an active popup itself raises Deactivated.
        base.OnClosing(e);
    }

    private void OnDashboard(object sender, RoutedEventArgs e) { Close(); DashboardRequested?.Invoke(); }
    private void OnSettings(object sender, RoutedEventArgs e) { Close(); SettingsRequested?.Invoke(); }
    private void OnExit(object sender, RoutedEventArgs e) { Close(); ExitRequested?.Invoke(); }

    private void QueuePlacement()
    {
        if (_placementQueued) return;
        _placementQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _placementQueued = false;
            // SizeToContent needs its first layout/render pass. Stay transparent until
            // the native bounds are placed so the default position never becomes visible.
            if (IsVisible && PlaceNearTray()) Opacity = 1;
        }));
    }

    private bool PlaceNearTray()
    {
        if (_placing) return false;
        _placing = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var cursor = _anchor;
            var monitor = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!NativeGetMonitorInfo(NativeMonitorFromPoint(cursor, 2), ref monitor)) return false;
            var area = monitor.Work;
            var dpi = VisualTreeHelper.GetDpi(this);
            MaxHeight = Math.Min(650, (area.Bottom - area.Top) / dpi.DpiScaleY - 16);
            MaxWidth = Math.Min(420, (area.Right - area.Left) / dpi.DpiScaleX - 16);
            AppListScroll.MaxHeight = Math.Max(40, MaxHeight - 170);
            UpdateLayout();
            // SizeToContent can finish after Loaded. Read the actual native bounds after rendering
            // and on subsequent size changes rather than projecting an early WPF DesiredSize.
            if (!NativeGetWindowRect(hwnd, out var bounds)) return false;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            // Leave the invoking icon/cursor clear, including icons in Explorer's overflow.
            var x = cursor.X < area.Left + (area.Right - area.Left) / 2 ? cursor.X + 12 : cursor.X - width - 12;
            var y = cursor.Y < area.Top + (area.Bottom - area.Top) / 2 ? cursor.Y + 12 : cursor.Y - height - 12;
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
            return NativeSetWindowPos(hwnd, new IntPtr(-1), x, y,
                0, 0, 0x1 | 0x10); // NOSIZE | NOACTIVATE; temporary user-opened popup above shell overflow
        }
        finally { _placing = false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool NativeGetCursorPos(out Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr NativeMonitorFromPoint(Point point, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool NativeGetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool NativeGetWindowRect(IntPtr hwnd, out Rect bounds);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool NativeSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
