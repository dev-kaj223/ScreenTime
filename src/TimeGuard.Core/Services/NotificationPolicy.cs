using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Pure candidates from committed facts. Skipped thresholds coalesce to the latest notice.</summary>
public static class NotificationPolicy
{
    public static NotificationRequest? Evaluate(PolicyDecision decision, DateOnly date, long remainingSeconds,
        bool running, DateTimeOffset now)
    {
        if (!running) return null;
        if (decision.Grace is { Phase: GracePhase.Active } grace && now < grace.ExpiresAtUtc)
        {
            var remaining = grace.ExpiresAtUtc - now;
            var kind = remaining <= TimeSpan.FromMinutes(1) ? NotificationKind.GraceFinalMinute :
                remaining <= TimeSpan.FromMinutes(5) ? NotificationKind.GraceFiveMinutes : NotificationKind.GraceStarted;
            return new($"grace:{grace.Id}:{kind}", decision.AppKey, decision.DisplayName, kind, now,
                kind == NotificationKind.GraceFinalMinute ? grace.ExpiresAtUtc : Min(now.AddSeconds(15), grace.ExpiresAtUtc),
                remaining, grace.Id, grace.ExpiresAtUtc);
        }
        if (!decision.MayContinue || remainingSeconds <= 0 || remainingSeconds > 600) return null;
        var quotaKind = remainingSeconds <= 300 ? NotificationKind.QuotaFiveMinutes : NotificationKind.QuotaTenMinutes;
        return new($"quota:{decision.AppKey}:{date:yyyy-MM-dd}:{quotaKind}", decision.AppKey, decision.DisplayName,
            quotaKind, now, now.AddSeconds(15), TimeSpan.FromSeconds(remainingSeconds));
    }

    public static bool IsCurrent(NotificationRequest request, PolicyDecision? decision, DateTimeOffset now)
    {
        if (decision is null || now >= request.ValidUntilUtc) return false;
        return request.Kind switch
        {
            NotificationKind.GraceStarted or NotificationKind.GraceFiveMinutes or NotificationKind.GraceFinalMinute =>
                decision.Grace is { Phase: GracePhase.Active } grace && grace.Id == request.EpisodeId &&
                request.GraceDeadlineUtc == grace.ExpiresAtUtc && now < grace.ExpiresAtUtc &&
                (request.Kind switch
                {
                    NotificationKind.GraceStarted => grace.ExpiresAtUtc - now > TimeSpan.FromMinutes(5),
                    NotificationKind.GraceFiveMinutes => grace.ExpiresAtUtc - now > TimeSpan.FromMinutes(1),
                    _ => grace.ExpiresAtUtc - now <= TimeSpan.FromMinutes(1)
                }),
            NotificationKind.Blocked => !decision.MayLaunch,
            _ => decision.MayContinue && decision.Grace is null
        };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
