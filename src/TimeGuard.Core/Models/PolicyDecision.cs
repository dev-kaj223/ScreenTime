namespace TimeGuard.Models;

public enum PolicyState { Available, TemporaryDowntime, DailyQuotaBlocked, Warning, QuotaExhaustedGrace }

[Flags]
public enum PolicyReason { None = 0, Downtime = 1, DailyQuotaExhausted = 2 }

/// <summary>Permission and warning intent are data; warning delivery never grants permission.</summary>
public sealed record PolicyDecision(
    string AppKey, string DisplayName, PolicyState State, PolicyReason PrimaryReason,
    PolicyReason Reasons, bool MayLaunch, bool MayContinue, bool TerminationRequired,
    bool WarnFiveMinutes, DateTimeOffset? DowntimeEnd = null, DateTimeOffset? NextDowntimeStart = null,
    DateTimeOffset? NextAvailability = null, GraceEpisode? Grace = null);
