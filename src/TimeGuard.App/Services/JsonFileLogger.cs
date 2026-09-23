using System.IO;
using System.Text.Json;

namespace TimeGuard.Services;

/// <summary>Local, bounded JSON Lines diagnostics. No configuration or window-content payloads.</summary>
public sealed class JsonFileLogger(RuntimeOptions runtime, long maxBytes = 1024 * 1024) : IAppLogger
{
    private readonly object _gate = new();
    public void Write(string level, string eventName, Exception? exception = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(runtime.Paths.LogsDirectory);
                var path = runtime.Paths.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
                {
                    // Two files, approximately 2 MiB total with the default limit.
                    File.Move(path, path + ".1", overwrite: true);
                }
                var record = new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    level, eventName, runId = runtime.RunId, profile = runtime.Profile.ToString(),
                    exception = exception is null ? null : new
                    {
                        type = exception.GetType().FullName,
                        message = Bounded(exception.Message),
                        stackTrace = Bounded(exception.StackTrace),
                        innerType = exception.InnerException?.GetType().FullName,
                        innerMessage = Bounded(exception.InnerException?.Message)
                    }
                };
                File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
            }
        }
        catch { /* No recursive logging or fallback writes outside the selected profile. */ }
    }

    private static string? Bounded(string? value) => value is { Length: > 16384 } ? value[..16384] : value;
}
