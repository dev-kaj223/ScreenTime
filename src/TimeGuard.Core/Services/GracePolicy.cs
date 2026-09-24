using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Pure continuation policy; launch permission never extends to replacements.</summary>
public static class GracePolicy
{
    public static PolicyDecision Apply(PolicyDecision normal, bool enabled,
        IEnumerable<GraceEpisode> episodes, DateTimeOffset now, ProcessInstance? instance = null)
    {
        if (!enabled) return normal;
        var relevant = episodes.Where(e => e.AppKey == normal.AppKey).ToArray();
        var active = relevant.FirstOrDefault(e => e.Phase == GracePhase.Active && now < e.ExpiresAtUtc);
        if (active is not null)
        {
            var captured = instance is not null && active.Processes.Contains(instance);
            return normal with { State = PolicyState.QuotaExhaustedGrace, MayLaunch = false,
                MayContinue = captured, TerminationRequired = instance is not null && !captured,
                WarnFiveMinutes = false, Grace = active };
        }
        // A carried survivor must stop at its deadline even with fresh next-day quota.
        if (instance is not null && relevant.Any(e =>
            (e.Phase == GracePhase.Expired || e.Phase == GracePhase.Active && now >= e.ExpiresAtUtc) && e.Processes.Contains(instance)))
            return normal with { MayContinue = false, TerminationRequired = true, WarnFiveMinutes = false };
        return normal;
    }
}
