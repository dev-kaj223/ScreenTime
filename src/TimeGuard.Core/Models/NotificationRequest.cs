namespace TimeGuard.Models;

public enum NotificationKind { QuotaTenMinutes, QuotaFiveMinutes, GraceStarted, GraceFiveMinutes, Blocked }

/// <summary>Display facts only. ValidUntilUtc expires the request, never permission.</summary>
public sealed record NotificationRequest(string ReceiptKey, string AppKey, string DisplayName,
    NotificationKind Kind, DateTimeOffset CreatedAtUtc, DateTimeOffset ValidUntilUtc,
    TimeSpan Remaining, string? EpisodeId = null, DateTimeOffset? GraceDeadlineUtc = null,
    PolicyReason Reason = PolicyReason.None, long ConfigurationRevision = 0, DateTimeOffset? NextAvailabilityUtc = null);
