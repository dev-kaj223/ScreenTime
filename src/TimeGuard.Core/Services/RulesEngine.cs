using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Pure Phase 2 policy. Persistence, processes and WPF are not dependencies.</summary>
public sealed class RulesEngine
{
    public PolicyDecision Evaluate(PolicySnapshot snapshot)
    {
        var scheduleRestricted = snapshot.Enabled &&
            snapshot.AllowedWindowStart is { } start && snapshot.AllowedWindowEnd is { } end &&
            (snapshot.LocalTime < start || snapshot.LocalTime > end);
        var quotaExhausted = snapshot.Enabled && snapshot.DailyLimitMinutes > 0 &&
            snapshot.UsageMinutes >= snapshot.DailyLimitMinutes;
        var reasons = (scheduleRestricted ? PolicyReason.OutsideAllowedWindow : PolicyReason.None) |
            (quotaExhausted ? PolicyReason.DailyQuotaExhausted : PolicyReason.None);
        var permitted = reasons == PolicyReason.None;
        var warning = snapshot.Enabled && permitted && snapshot.DailyLimitMinutes > 0 &&
            snapshot.DailyLimitMinutes - snapshot.UsageMinutes <= 5;
        return new(snapshot.AppKey, snapshot.DisplayName,
            scheduleRestricted ? PolicyState.TemporaryScheduleRestriction :
            quotaExhausted ? PolicyState.DailyQuotaBlocked :
            warning ? PolicyState.Warning : PolicyState.Available,
            scheduleRestricted ? PolicyReason.OutsideAllowedWindow :
            quotaExhausted ? PolicyReason.DailyQuotaExhausted : PolicyReason.None,
            reasons, permitted, permitted, !permitted && snapshot.IsRunning,
            warning && snapshot.IsRunning && !snapshot.WarningSent);
    }
}
