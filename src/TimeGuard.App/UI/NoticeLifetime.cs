using TimeGuard.Models;

namespace TimeGuard.UI;

/// <summary>Caps display lifetime only. Never calls policy, persistence, or enforcement.</summary>
internal sealed class NoticeLifetime
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly DateTimeOffset _closeAt;
    private readonly TimeSpan _maximum;
    private TimeSpan _remaining;

    internal NoticeLifetime(NotificationRequest request, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var now = _time.GetUtcNow();
        _started = _time.GetTimestamp();
        _maximum = request.Kind == NotificationKind.GraceFinalMinute ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(6);
        _closeAt = request.Kind == NotificationKind.GraceFinalMinute ? request.GraceDeadlineUtc ?? now : now + _maximum;
        if (request.ValidUntilUtc < _closeAt) _closeAt = request.ValidUntilUtc;
        if (_closeAt - now < _maximum) _maximum = _closeAt - now;
        _remaining = _maximum;
    }

    internal TimeSpan Remaining
    {
        get
        {
            var monotonic = _maximum - _time.GetElapsedTime(_started);
            var utc = _closeAt - _time.GetUtcNow();
            _remaining = new TimeSpan(Math.Max(0, Math.Min(_remaining.Ticks, Math.Min(monotonic.Ticks, utc.Ticks))));
            return _remaining;
        }
    }
}
