using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TimeGuard.Helpers;
using TimeGuard.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace TimeGuard.UI;

internal sealed class PassiveNoticeWindow : Window
{
    private readonly DispatcherTimer _lifetime = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Action<NoticeDiagnostic>? _diagnostic;
    private readonly Func<bool> _current;
    private DateTimeOffset _closeAt;
    private long _shownTimestamp;
    private readonly IntPtr _foregroundBefore;
    private IntPtr _hwnd;
    internal NotificationRequest Request { get; }

    internal PassiveNoticeWindow(NotificationRequest request, Func<bool> current, Action<NoticeDiagnostic>? diagnostic)
    {
        Request = request;
        _current = current;
        _diagnostic = diagnostic;
        _foregroundBefore = NonActivatingWindowHelper.GetForegroundWindow();
        Title = "ScreenTime notice";
        Width = 420; Height = 190;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)), Padding = new Thickness(20),
            Child = new TextBlock { Text = Text(request, DateTimeOffset.UtcNow), TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White, FontSize = 16, Focusable = false, IsHitTestVisible = false }
        };
        SourceInitialized += (_, _) => _hwnd = NonActivatingWindowHelper.Configure(this, _foregroundBefore);
        ContentRendered += (_, _) => Sample("Shown");
        _lifetime.Tick += (_, _) =>
        {
            Sample("Sample");
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_shownTimestamp) >= TimeSpan.FromSeconds(6) ||
                DateTimeOffset.UtcNow >= _closeAt || !_current()) Close();
        };
        Closed += (_, _) => { _lifetime.Stop(); Sample("Closed"); };
        Loaded += (_, _) =>
        {
            _shownTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _closeAt = DateTimeOffset.UtcNow.AddSeconds(6);
            _lifetime.Start();
        };
    }

    private void Sample(string stage)
    {
        if (_diagnostic is null) return;
        _diagnostic(new(stage, DateTimeOffset.UtcNow, _hwnd.ToInt64(), _foregroundBefore.ToInt64(),
            NonActivatingWindowHelper.GetForegroundWindow().ToInt64(), NonActivatingWindowHelper.GetActiveWindow().ToInt64(),
            NonActivatingWindowHelper.GetFocus().ToInt64(), IsKeyboardFocusWithin, IsMouseCaptured,
            NonActivatingWindowHelper.GetWindowLong(_hwnd, -20)));
    }

    internal static string Text(NotificationRequest request, DateTimeOffset now)
    {
        var remaining = request.GraceDeadlineUtc is { } deadline ? deadline - now : request.Remaining;
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return request.Kind switch
        {
            NotificationKind.GraceStarted or NotificationKind.GraceFiveMinutes =>
                (request.Kind == NotificationKind.GraceFiveMinutes ? "Final warning. " : "") +
                $"{request.DisplayName}: daily quota exhausted. Finish your current game. ScreenTime will close the app in {minutes} minute{(minutes == 1 ? "" : "s")} ({request.GraceDeadlineUtc!.Value.ToLocalTime():HH:mm:ss}). New launches are not allowed.",
            NotificationKind.Blocked => $"{request.DisplayName}: launch blocked. " +
                (request.Reason.HasFlag(PolicyReason.Downtime) ? "Downtime is active." : "Daily quota is exhausted.") +
                (request.NextAvailabilityUtc is { } available ? $" Next availability: {available.ToLocalTime():ddd HH:mm}." : ""),
            _ => $"{request.DisplayName}: about {minutes} minutes of daily quota remaining."
        };
    }
}

internal sealed record NoticeDiagnostic(string Stage, DateTimeOffset AtUtc, long Hwnd, long ForegroundBefore,
    long Foreground, long Active, long Focus, bool KeyboardFocusWithin, bool MouseCaptured, int ExtendedStyles);
