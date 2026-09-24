namespace TimeGuard.Models;

public enum PolicyState { Available, TemporaryScheduleRestriction, DailyQuotaBlocked, Warning }

[Flags]
public enum PolicyReason { None = 0, OutsideAllowedWindow = 1, DailyQuotaExhausted = 2 }

/// <summary>Permission and warning intent are data; warning delivery never grants permission.</summary>
public sealed record PolicyDecision(
    string AppKey, string DisplayName, PolicyState State, PolicyReason PrimaryReason,
    PolicyReason Reasons, bool MayLaunch, bool MayContinue, bool TerminationRequired,
    bool WarnFiveMinutes);
