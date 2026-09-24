namespace TimeGuard.Models;

public enum GracePhase { Active, CompletedByExit, Expired }

/// <summary>Immutable durable facts. Captured identities and deadline never change.</summary>
public sealed record GraceEpisode(
    string Id, string AppKey, DateOnly QuotaDate, DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc, GracePhase Phase, DateTimeOffset? EndedAtUtc,
    IReadOnlyList<ProcessInstance> Processes)
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(20);
}

public sealed record QuotaCrossing(string AppKey, DateOnly QuotaDate,
    DateTimeOffset ExhaustedAtUtc, IReadOnlyList<ProcessInstance> EligibleProcesses);
