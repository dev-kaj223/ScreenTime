using TimeGuard.Models;

namespace TimeGuard.UI;

/// <summary>Presentation only; all deadline values arrive in committed Core facts.</summary>
internal static class NoticePresentation
{
    internal static (string Heading, string Accent) Style(NotificationRequest request) => request.Kind switch
    {
        NotificationKind.QuotaTenMinutes => ("TIME REMAINING", "#9FC8F4"),
        NotificationKind.QuotaFiveMinutes => ("5 MINUTES LEFT", "#E9C46A"),
        NotificationKind.GraceStarted => ("FINISH YOUR SESSION", "#F4AD72"),
        NotificationKind.GraceFiveMinutes => ("FINAL WARNING", "#FF9999"),
        NotificationKind.GraceFinalMinute => ("FINAL MINUTE", "#FF9999"),
        _ => (request.Reason.HasFlag(PolicyReason.Downtime) ? "APP BLOCKED" : "TIME EXPIRED", "#FF9999")
    };

    internal static string Body(NotificationRequest request, DateTimeOffset now)
    {
        // Bound untrusted/user-entered labels without truncating the policy explanation.
        var name = request.DisplayName.Length > 80 ? request.DisplayName[..79] + "…" : request.DisplayName;
        var remaining = request.GraceDeadlineUtc is { } deadline ? deadline - now : request.Remaining;
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return request.Kind switch
        {
            NotificationKind.GraceFinalMinute => $"{name} will close at {request.GraceDeadlineUtc!.Value.ToLocalTime():HH:mm:ss}.\nFinish your current game now.",
            NotificationKind.GraceStarted or NotificationKind.GraceFiveMinutes =>
                $"{name}: daily quota exhausted. Finish your current game.\nThe app will close in {minutes} minute{(minutes == 1 ? "" : "s")} ({request.GraceDeadlineUtc!.Value.ToLocalTime():HH:mm:ss}). New launches are not allowed.",
            NotificationKind.Blocked => $"{name}: launch blocked. " +
                (request.Reason.HasFlag(PolicyReason.Downtime) ? "Downtime is active." : "Daily quota is exhausted.") +
                (request.NextAvailabilityUtc is { } available ? $" Next availability: {available.ToLocalTime():ddd HH:mm}." : ""),
            _ => $"{name}: about {minutes} minutes of daily quota remaining."
        };
    }
}
