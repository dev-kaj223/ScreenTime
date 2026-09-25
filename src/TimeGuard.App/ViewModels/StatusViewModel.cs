using System.ComponentModel;
using System.Collections.ObjectModel;
using TimeGuard.Models;

namespace TimeGuard.ViewModels;

internal sealed class StatusViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AppStatusRow> Apps { get; } = [];
    public string Summary { get; private set; } = "Waiting for the first observation…";
    public string Tooltip { get; private set; } = "ScreenTime — Starting";

    public void Refresh(StatusSnapshot? snapshot, DateTimeOffset now)
    {
        var current = Apps.ToDictionary(a => a.AppKey);
        var sorted = snapshot?.Apps.Select(a =>
            {
                if (!current.TryGetValue(a.Facts.AppKey, out var row)) row = new AppStatusRow(a, now);
                else row.Refresh(a, now);
                return row;
            })
            .OrderBy(a => a.SortPriority).ThenBy(a => a.RemainingSeconds ?? long.MaxValue)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.AppKey, StringComparer.Ordinal)
            .ToArray() ?? [];
        foreach (var row in Apps.Where(a => !sorted.Contains(a)).ToArray()) Apps.Remove(row);
        for (var index = 0; index < sorted.Length; index++)
        {
            var old = Apps.IndexOf(sorted[index]);
            if (old < 0) Apps.Insert(index, sorted[index]);
            else if (old != index) Apps.Move(old, index);
        }
        Summary = snapshot is null ? "Waiting for the first observation…" : Apps.Count == 0 ?
            "No enabled applications configured" : $"Observed {snapshot.ObservedAtUtc.ToLocalTime():T} · Availability assumes no further use";
        var tooltip = snapshot is null ? "ScreenTime — Starting" : Apps.Count switch
        {
            0 => "ScreenTime — No apps configured",
            1 => $"ScreenTime — {Apps[0].DisplayName}: {Apps[0].TooltipStatus}",
            _ => $"ScreenTime — {Apps.Count} apps monitored · {Apps.Count(a => a.Restricted)} restricted"
        };
        // WinForms supports at most 127 UTF-16 characters. Avoid splitting a surrogate pair.
        Tooltip = tooltip.Length <= 127 ? tooltip : tooltip[..(char.IsHighSurrogate(tooltip[125]) ? 125 : 126)] + "…";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
    }
}

internal sealed class AppStatusRow : INotifyPropertyChanged
{
    private AppStatus _status;
    private DateTimeOffset _now;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AppStatusRow(AppStatus status, DateTimeOffset now) { _status = status; _now = now; }
    public void Refresh(AppStatus status, DateTimeOffset now)
    { _status = status; _now = now; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public string AppKey => _status.Facts.AppKey;
    public string DisplayName => _status.Facts.DisplayName;
    public long? RemainingSeconds => _status.Facts.DailyLimitMinutes == 0 ? null :
        Math.Max(0, _status.Facts.DailyLimitMinutes * 60L - _status.Facts.QuotaSeconds);
    public bool HasGrace => _status.Decision.Grace is not null;
    public bool Restricted => !_status.Decision.MayLaunch;
    public int SortPriority => HasGrace ? 0 : Restricted ? 1 : _status.Facts.IsRunning ? 2 : 3;
    public string State => HasGrace ? (_status.Decision.Grace!.ExpiresAtUtc <= _now ? "SESSION TIME EXPIRED" : "FINISH SESSION") :
        _status.Facts.Downtime.IsActive ? "DOWNTIME" : Restricted ? "DAILY LIMIT REACHED" : "AVAILABLE";
    public string Accent => HasGrace ? "#F4AD72" : Restricted ? "#FF9999" : "#9FC8F4";
    public string Running => _status.Facts.IsRunning ? "Running at last observation" : "Not observed running";
    public string Usage => $"Used {Duration(_status.Facts.QuotaSeconds)} / {(_status.Facts.DailyLimitMinutes == 0 ? "Unlimited" : Duration(_status.Facts.DailyLimitMinutes * 60L))} daily allowance";
    public string Remaining => RemainingSeconds is { } seconds ? $"{Duration(seconds)} remaining{(Restricted ? " · unavailable for new sessions" : "")}" :
        $"Unlimited allowance{(Restricted ? " · unavailable for new sessions" : "")}";
    public string Observed => $"Observed today: {Duration(_status.ObservedSeconds)} · includes {Duration(_status.GraceSeconds)} in grace";
    public string GraceCountdown => _status.Decision.Grace is { } grace ?
        $"{Math.Max(0, (int)Math.Ceiling((grace.ExpiresAtUtc - _now).TotalSeconds)) / 60}:{Math.Max(0, (int)Math.Ceiling((grace.ExpiresAtUtc - _now).TotalSeconds)) % 60:00} remaining" : "";
    public string GraceDetail => _status.Decision.Grace is { } grace ?
        $"Daily limit reached · New sessions are blocked\nSession ends at {grace.ExpiresAtUtc.ToLocalTime():T}" : "";
    public string Downtime => _status.Facts.Downtime.IsActive ? "Downtime active" : "Downtime inactive";
    public string Quota => _status.Decision.Reasons.HasFlag(PolicyReason.DailyQuotaExhausted) ? "Daily quota exhausted" : "Daily quota available";
    public string NextDowntime => $"Next downtime: {Format(_status.Decision.NextDowntimeStart, "None scheduled")}";
    public string NextAvailability => $"New-session availability: {(HasGrace && _status.Decision.NextAvailability <= _now ? "After this session ends, subject to current policy" : Format(_status.Decision.NextAvailability, "None scheduled"))}";
    public string TooltipStatus => HasGrace ? $"Finish session · {Math.Max(0, Math.Ceiling((_status.Decision.Grace!.ExpiresAtUtc - _now).TotalMinutes))} min remaining" : Restricted ? State.ToLowerInvariant() :
        RemainingSeconds is { } s ? $"{Math.Ceiling(s / 60d):0} min remaining" : "Unlimited";
    private static string Format(DateTimeOffset? value, string missing) => value?.ToLocalTime().ToString("g") ?? missing;
    private static string Duration(long seconds) => $"{seconds / 60}m {seconds % 60:00}s";
}
