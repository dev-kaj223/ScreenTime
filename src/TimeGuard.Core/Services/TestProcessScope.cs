using System.Text.Json;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Preserves Phase 1 test ownership for discovery AND enforcement.</summary>
public sealed class TestProcessScope(AppDataPaths paths, IAppLogger? logger = null)
{
    public bool Contains(ProcessInstance instance)
    {
        if (instance.AppKey != "screentime.testprocess") return false;
        try
        {
            if (!File.Exists(paths.OwnedProcessesPath)) return false;
            var owned = JsonSerializer.Deserialize<OwnedProcessIdentity[]>(File.ReadAllText(paths.OwnedProcessesPath)) ?? [];
            return owned.Any(p => p.Id == instance.ProcessId && p.StartTimeUtcTicks == instance.StartTimeUtcTicks &&
                ProcessInstance.NormalizeKey(p.ProcessName) == instance.AppKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.TryWrite("Error", "TestProcessOwnershipReadFailed", ex);
            return false;
        }
    }
}
