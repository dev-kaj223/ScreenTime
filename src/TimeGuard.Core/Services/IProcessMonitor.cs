using TimeGuard.Models;

namespace TimeGuard.Services;

public interface IProcessMonitor
{
    IReadOnlyList<ProcessInstance> Snapshot(IReadOnlyCollection<string> candidateKeys);
    // Absence from a snapshot alone is insufficient when inspection can fail.
    bool ConfirmedExited(ProcessInstance instance);
}
