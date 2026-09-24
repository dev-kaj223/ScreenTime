using System.ComponentModel;
using System.Diagnostics;
using TimeGuard.Models;

namespace TimeGuard.Services;

public sealed class WindowsProcessMonitor(IAppLogger? logger = null,
    Func<ProcessInstance, bool>? isAllowedTarget = null) : IProcessMonitor
{
    public IReadOnlyList<ProcessInstance> Snapshot(IReadOnlyCollection<string> candidateKeys)
    {
        var instances = new List<ProcessInstance>();
        using var current = Process.GetCurrentProcess();
        foreach (var key in candidateKeys.Select(ProcessInstance.NormalizeKey).Distinct())
        {
            // Enumerate only configured names; titles are not required or collected.
            foreach (var process in Process.GetProcessesByName(key))
            {
                using (process)
                {
                    try
                    {
                        var instance = new ProcessInstance(process.ProcessName, process.Id,
                            process.StartTime.ToUniversalTime().Ticks, process.SessionId);
                        if (instance.SessionId == current.SessionId &&
                            (isAllowedTarget?.Invoke(instance) ?? true)) instances.Add(instance);
                    }
                    catch (InvalidOperationException) { /* Exited during observation. */ }
                    catch (Win32Exception ex) { logger.TryWrite("Error", "ProcessObservationAccessFailed", ex); }
                }
            }
        }
        return instances.AsReadOnly();
    }
}
