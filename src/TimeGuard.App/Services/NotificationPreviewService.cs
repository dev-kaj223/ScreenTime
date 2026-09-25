using TimeGuard.Models;
using TimeGuard.UI;

namespace TimeGuard.Services;

/// <summary>Only synthetic display facts; no monitor, database, process adapter or policy dependencies.</summary>
internal sealed class NotificationPreviewService : IDisposable
{
    private PassiveNoticeWindow? _window;
    internal static NotificationRequest Create(NotificationKind kind, NotificationPreferences options, DateTimeOffset now)
    {
        var seconds = kind == NotificationKind.GraceFinalMinute ? options.CountdownSeconds :
            kind == NotificationKind.GraceStarted ? 1200 : kind == NotificationKind.QuotaTenMinutes ? 600 : 300;
        return new("preview", "preview", "Example App", kind, now,
            now.AddSeconds(kind == NotificationKind.GraceFinalMinute ? seconds : 15), TimeSpan.FromSeconds(seconds),
            "preview", now.AddSeconds(seconds));
    }

    public void Show(NotificationKind kind, NotificationPreferences options)
    {
        options.Validate();
        _window?.Close();
        var request = Create(kind, options, DateTimeOffset.UtcNow);
        _window = new PassiveNoticeWindow(request, () => DateTimeOffset.UtcNow < request.ValidUntilUtc, null, options);
        _window.Show();
    }
    public void Dispose() { _window?.Close(); _window = null; }
}
