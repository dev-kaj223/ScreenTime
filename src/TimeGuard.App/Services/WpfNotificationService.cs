using System.Windows.Threading;
using TimeGuard.Models;
using TimeGuard.UI;

namespace TimeGuard.Services;

/// <summary>One brief surface; polling this bounded mailbox never calls back into enforcement.</summary>
internal sealed class WpfNotificationService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PassiveNoticeWindow? _window;
    private readonly Dictionary<string, NotificationRequest> _countdowns = [];

    internal WpfNotificationService(Func<NotificationRequest?> read, Func<NotificationRequest, bool> current,
        IAppLogger? logger, Action<NoticeDiagnostic>? diagnostic = null, Func<NotificationPreferences>? preferences = null)
    {
        _timer.Tick += (_, _) =>
        {
            try
            {
                var options = preferences?.Invoke() ?? NotificationPreferences.Standard;
                NotificationRequest? latest = null;
                for (var i = 0; i < 8; i++)
                {
                    var next = read();
                    if (next is null) break;
                    if (!current(next) || !options.Allows(next.Kind)) continue;
                    if (next.Kind == NotificationKind.GraceFinalMinute)
                    {
                        if (_countdowns.Count < 8 || _countdowns.ContainsKey(next.AppKey)) _countdowns[next.AppKey] = next;
                    }
                    else latest = next;
                }
                foreach (var countdown in _countdowns.Values.ToArray())
                {
                    if (!current(countdown) || !options.Allows(countdown.Kind)) { _countdowns.Remove(countdown.AppKey); continue; }
                    if (options.Ready(countdown, DateTimeOffset.UtcNow)) { latest = countdown; _countdowns.Remove(countdown.AppKey); }
                }
                if (latest is null) return;
                _window?.Close();
                var request = latest;
                var window = new PassiveNoticeWindow(request, () => current(request) &&
                    (preferences?.Invoke() ?? NotificationPreferences.Standard).Allows(request.Kind), diagnostic, options);
                _window = window;
                window.Closed += (_, _) => { if (_window == window) _window = null; };
                window.Show();
            }
            catch (Exception ex)
            {
                _window?.Close();
                _window = null;
                logger.TryWrite("Error", "NotificationFailed", ex);
            }
        };
        _timer.Start();
    }

    public void Dispose() { _timer.Stop(); _countdowns.Clear(); _window?.Close(); _window = null; }
}
