using System.Threading.Channels;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Bounded best-effort receipt IO and delivery, off the enforcement worker.</summary>
internal sealed class NotificationOutbox(IStateStore store, Func<NotificationRequest, bool> current, IAppLogger? logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NotificationRequest> _pending = [];
    private readonly Channel<NotificationRequest> _ready = Channel.CreateBounded<NotificationRequest>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _worker;
    private bool _stopping;
    internal Task Completion { get { lock (_gate) return _worker ?? Task.CompletedTask; } }
    internal bool TryRead(out NotificationRequest? request) => _ready.Reader.TryRead(out request);
    internal void Queue(NotificationRequest request)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (_pending.Count == 8 && !_pending.ContainsKey(request.AppKey)) _pending.Remove(_pending.Keys.First());
            _pending[request.AppKey] = request;
            _worker ??= Task.Run(Deliver);
        }
    }
    private void Deliver()
    {
        while (true)
        {
            NotificationRequest request;
            lock (_gate)
            {
                if (_stopping || _pending.Count == 0) { _pending.Clear(); _worker = null; return; }
                request = _pending.Values.First();
                _pending.Remove(request.AppKey);
            }
            try
            {
                // Live final-minute state may resume after restart; it has no durable delivery receipt or per-second writes.
                if (current(request) && (request.Kind is NotificationKind.Blocked or NotificationKind.GraceFinalMinute || store.TryRecordNotification(request)) && current(request))
                    _ready.Writer.TryWrite(request);
            }
            catch (Exception ex) { logger.TryWrite("Error", "NotificationReceiptFailed", ex); }
        }
    }
    internal Task StopAsync()
    {
        lock (_gate) { _stopping = true; _pending.Clear(); return _worker ?? Task.CompletedTask; }
    }
}
