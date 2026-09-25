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
            NotificationKind.GraceFinalMinute =>
                $"{name} has less than a minute left in the current session. This session will end at {DisplayTime.Clock(request.GraceDeadlineUtc!.Value)}.",
            NotificationKind.GraceStarted =>
                $"{name} has reached its daily limit. Finish your current session. This session will end in {minutes} minutes at {DisplayTime.Clock(request.GraceDeadlineUtc!.Value)}. New sessions are not allowed.",
            NotificationKind.GraceFiveMinutes =>
                $"{name} has 5 minutes left in the current session. This session will end at {DisplayTime.Clock(request.GraceDeadlineUtc!.Value)}. New sessions are not allowed.",
            NotificationKind.Blocked when request.Reason.HasFlag(PolicyReason.Downtime) =>
                $"{name} is unavailable during downtime." +
                (request.NextAvailabilityUtc is { } available ? $" Next availability is {DisplayTime.Availability(available, now)}." : ""),
            NotificationKind.Blocked => $"{name} has reached its daily limit and cannot be opened again today.",
            NotificationKind.QuotaFiveMinutes => $"{name} has about 5 minutes of daily time remaining.",
            _ => $"{name} has about {minutes} minutes of daily time remaining."
        };
    }
}
