namespace TimeGuard.Models;

/// <summary>Identity of one observation, never a command to kill by name.</summary>
public sealed record ProcessInstance
{
    public string AppKey { get; }
    public int ProcessId { get; }
    public long StartTimeUtcTicks { get; }
    public int SessionId { get; }

    public ProcessInstance(string appKey, int processId, long startTimeUtcTicks, int sessionId)
    {
        AppKey = NormalizeKey(appKey);
        ProcessId = processId;
        StartTimeUtcTicks = startTimeUtcTicks;
        SessionId = sessionId;
    }

    public static string NormalizeKey(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return key.EndsWith(".exe", StringComparison.Ordinal) ? key[..^4] : key;
    }
}
