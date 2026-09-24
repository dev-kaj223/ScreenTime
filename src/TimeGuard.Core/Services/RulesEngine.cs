using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Pure Phase 3 policy. Persistence, processes and WPF are not dependencies.</summary>
public sealed class RulesEngine
{
    public PolicyDecision Evaluate(PolicySnapshot snapshot)
    {
        var scheduleRestricted = snapshot.Enabled && snapshot.Downtime.IsActive;
        var quotaExhausted = snapshot.Enabled && snapshot.DailyLimitMinutes > 0 &&
            snapshot.QuotaSeconds >= snapshot.DailyLimitMinutes * 60L;
        var reasons = (scheduleRestricted ? PolicyReason.Downtime : PolicyReason.None) |
            (quotaExhausted ? PolicyReason.DailyQuotaExhausted : PolicyReason.None);
        var permitted = reasons == PolicyReason.None;
        var warning = snapshot.Enabled && permitted && snapshot.DailyLimitMinutes > 0 &&
            snapshot.DailyLimitMinutes - snapshot.UsageMinutes <= 5;
        return new(snapshot.AppKey, snapshot.DisplayName,
            scheduleRestricted ? PolicyState.TemporaryDowntime :
            quotaExhausted ? PolicyState.DailyQuotaBlocked :
            warning ? PolicyState.Warning : PolicyState.Available,
            scheduleRestricted ? PolicyReason.Downtime :
            quotaExhausted ? PolicyReason.DailyQuotaExhausted : PolicyReason.None,
            reasons, permitted, permitted, !permitted && snapshot.IsRunning,
            warning && snapshot.IsRunning && !snapshot.WarningSent,
            snapshot.Downtime.CurrentEnd, snapshot.Downtime.NextStart, snapshot.NextAvailability);
    }
}
