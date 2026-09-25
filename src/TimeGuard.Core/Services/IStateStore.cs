using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Only the existing persistence operations used by the serialized coordinator.</summary>
public interface IStateStore
{
    DailyLog LoadLog(DateOnly date);
    void UpsertUsageEntry(DateOnly date, UsageEntry entry);
    void SaveUsage(IEnumerable<DailyLog> logs);
    IReadOnlyList<GraceEpisode> LoadGraceEpisodes();
    IReadOnlyList<GraceEpisode> CommitObservation(IEnumerable<DailyLog> logs, IEnumerable<GraceEpisode> episodes);
    bool TryRecordNotification(NotificationRequest request);
    int OpenSession(string processName, string windowTitle = "", bool isPassive = false);
    void CloseSession(int sessionId, double timeSinceBreakMins = 0);
}
