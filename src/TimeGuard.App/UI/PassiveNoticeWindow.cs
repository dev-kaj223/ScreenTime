using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
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
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private NoticeLifetime? _displayLifetime;
    private readonly TextBlock _countdownText;
    private readonly IntPtr _foregroundBefore;
    private IntPtr _hwnd;
    internal NotificationRequest Request { get; }

    internal PassiveNoticeWindow(NotificationRequest request, Func<bool> current, Action<NoticeDiagnostic>? diagnostic,
        NotificationPreferences? preferences = null)
    {
        Request = request;
        _current = current;
        _diagnostic = diagnostic;
        _foregroundBefore = NonActivatingWindowHelper.GetForegroundWindow();
        Title = "ScreenTime notice";
        Width = 380; MinWidth = 280; MaxWidth = 420;
        MinHeight = 128; MaxHeight = 360; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        var (heading, accentColor) = NoticePresentation.Style(request);
        accentColor = preferences?.ColorFor(request.Kind) ?? accentColor;
        var accent = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(accentColor));
        var stack = new StackPanel();
        var brand = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10) };
        var logo = new System.Windows.Controls.Image { Width = 24, Height = 24, Stretch = Stretch.Uniform };
        logo.SetResourceReference(System.Windows.Controls.Image.SourceProperty, "ScreenTimeBrandImage");
        AutomationProperties.SetAutomationId(logo, "ScreenTimeBrand");
        brand.Children.Add(logo);
        brand.Children.Add(new TextBlock { Text = "ScreenTime", FontSize = 12, Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        stack.Children.Add(brand);
        var header = Label(heading, 14, accent);
        header.FontWeight = FontWeights.Bold;
        AutomationProperties.SetAutomationId(header, "NoticeHeading");
        stack.Children.Add(header);
        _countdownText = Label("", 32, accent);
        _countdownText.FontWeight = FontWeights.SemiBold;
        _countdownText.Visibility = request.Kind == NotificationKind.GraceFinalMinute ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_countdownText, "NoticeCountdown");
        stack.Children.Add(_countdownText);
        var body = Label(NoticePresentation.Body(request, DateTimeOffset.UtcNow), 16, Brushes.White);
        body.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetAutomationId(body, "NoticeBody");
        stack.Children.Add(body);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)), Padding = new Thickness(18, 14, 18, 16),
            BorderBrush = accent, BorderThickness = new Thickness(0, 3, 0, 0), Child = stack
        };
        SourceInitialized += (_, _) => _hwnd = NonActivatingWindowHelper.Configure(this, _foregroundBefore);
        SizeChanged += (_, _) => { if (_hwnd != IntPtr.Zero) NonActivatingWindowHelper.Place(this, _hwnd, _foregroundBefore); };
        ContentRendered += (_, _) => Sample("Shown");
        _lifetime.Tick += (_, _) =>
        {
            Sample("Sample");
            if (_displayLifetime!.Remaining <= TimeSpan.Zero || !_current()) Close();
        };
        _countdown.Tick += (_, _) => UpdateCountdown();
        Closed += (_, _) => { _lifetime.Stop(); _countdown.Stop(); Sample("Closed"); };
        Loaded += (_, _) =>
        {
            _displayLifetime = new NoticeLifetime(request);
            if (request.Kind == NotificationKind.GraceFinalMinute) { UpdateCountdown(); _countdown.Start(); }
            _lifetime.Start();
        };
    }

    private static TextBlock Label(string text, double size, System.Windows.Media.Brush color) => new()
    {
        Text = text, FontSize = size, Foreground = color, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Focusable = false, IsHitTestVisible = false
    };

    private void UpdateCountdown()
    {
        var seconds = (int)Math.Ceiling(_displayLifetime!.Remaining.TotalSeconds);
        _countdownText.Text = $"{seconds / 60}:{seconds % 60:00}";
    }

    private void Sample(string stage)
    {
        if (_diagnostic is null) return;
        _diagnostic(new(stage, DateTimeOffset.UtcNow, _hwnd.ToInt64(), _foregroundBefore.ToInt64(),
            NonActivatingWindowHelper.GetForegroundWindow().ToInt64(), NonActivatingWindowHelper.GetActiveWindow().ToInt64(),
            NonActivatingWindowHelper.GetFocus().ToInt64(), IsKeyboardFocusWithin, IsMouseCaptured,
            NonActivatingWindowHelper.GetWindowLong(_hwnd, -20), _countdownText.Text, ActualWidth, ActualHeight));
    }
}

internal sealed record NoticeDiagnostic(string Stage, DateTimeOffset AtUtc, long Hwnd, long ForegroundBefore,
    long Foreground, long Active, long Focus, bool KeyboardFocusWithin, bool MouseCaptured, int ExtendedStyles,
    string Countdown, double Width, double Height);
