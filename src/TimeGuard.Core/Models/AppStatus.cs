namespace TimeGuard.Models;

/// <summary>Copied, committed per-app facts for readers. No permission or mutation API.</summary>
public sealed record AppStatus(PolicySnapshot Facts, PolicyDecision Decision,
    long ObservedSeconds, long GraceSeconds);

public sealed record StatusSnapshot(DateTimeOffset ObservedAtUtc, IReadOnlyList<AppStatus> Apps);
