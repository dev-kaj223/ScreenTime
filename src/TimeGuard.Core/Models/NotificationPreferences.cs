using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimeGuard.Models;

public enum NotificationPreset { Standard, Minimal, Custom }

/// <summary>Presentation preferences only. No policy, process or grace authority.</summary>
public sealed record NotificationPreferences
{
    public NotificationPreset Preset { get; init; } = NotificationPreset.Standard;
    public bool QuotaTenMinutes { get; init; } = true;
    public bool QuotaFiveMinutes { get; init; } = true;
    public bool GraceStarted { get; init; } = true;
    public bool GraceFiveMinutes { get; init; } = true;
    public bool GraceFinalMinute { get; init; } = true;
    public bool Blocked { get; init; } = true;
    public int CountdownSeconds { get; init; } = 60;
    public string? InformationalColor { get; init; }
    public string? WarningColor { get; init; }
    public string? CriticalColor { get; init; }

    public static NotificationPreferences Standard => new();
    public static NotificationPreferences Minimal => new() { Preset = NotificationPreset.Minimal,
        QuotaTenMinutes = false, QuotaFiveMinutes = false, GraceFiveMinutes = false };

    public void Validate()
    {
        if (!Enum.IsDefined(Preset) || CountdownSeconds is < 1 or > 60)
            throw new ArgumentException("Countdown duration must be between 1 and 60 seconds.");
        foreach (var color in new[] { InformationalColor, WarningColor, CriticalColor })
            if (color is not null && !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
                throw new ArgumentException("Use a color in #RRGGBB format, or leave blank for the default.");
    }

    public bool Allows(NotificationKind kind) => kind switch
    {
        NotificationKind.QuotaTenMinutes => QuotaTenMinutes,
        NotificationKind.QuotaFiveMinutes => QuotaFiveMinutes,
        NotificationKind.GraceStarted => GraceStarted,
        NotificationKind.GraceFiveMinutes => GraceFiveMinutes,
        NotificationKind.GraceFinalMinute => GraceFinalMinute,
        NotificationKind.Blocked => Blocked,
        _ => false
    };

    public string? ColorFor(NotificationKind kind) => kind switch
    {
        NotificationKind.QuotaTenMinutes => InformationalColor,
        NotificationKind.QuotaFiveMinutes or NotificationKind.GraceStarted => WarningColor,
        _ => CriticalColor
    };

    public bool Ready(NotificationRequest request, DateTimeOffset now) => Allows(request.Kind) &&
        now < request.ValidUntilUtc && (request.Kind != NotificationKind.GraceFinalMinute ||
            request.GraceDeadlineUtc is { } deadline && deadline > now && deadline - now <= TimeSpan.FromSeconds(CountdownSeconds));

    public string Serialize() { Validate(); return JsonSerializer.Serialize(this); }
    public static NotificationPreferences Parse(string? json)
    {
        if (json is null) return Standard;
        var result = JsonSerializer.Deserialize<NotificationPreferences>(json) ?? throw new ArgumentException("Missing notification preferences.");
        result.Validate();
        return result;
    }
}
