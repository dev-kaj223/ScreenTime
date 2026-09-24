using System.Windows.Threading;
using TimeGuard.Models;
using TimeGuard.UI;

namespace TimeGuard.Services;

/// <summary>One brief surface; polling this bounded mailbox never calls back into enforcement.</summary>
internal sealed class WpfNotificationService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PassiveNoticeWindow? _window;

    internal WpfNotificationService(Func<NotificationRequest?> read, Func<NotificationRequest, bool> current,
        IAppLogger? logger, Action<NoticeDiagnostic>? diagnostic = null)
    {
        _timer.Tick += (_, _) =>
        {
            try
            {
                NotificationRequest? latest = null;
                for (var i = 0; i < 8; i++)
                {
                    var next = read();
                    if (next is null) break;
                    if (current(next)) latest = next;
                }
                if (latest is null) return;
                _window?.Close();
                var request = latest;
                var window = new PassiveNoticeWindow(request, () => current(request), diagnostic);
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

    public void Dispose() { _timer.Stop(); _window?.Close(); _window = null; }
}
