using TimeGuard.Models;

namespace TimeGuard.Services;

public interface IProcessMonitor
{
    IReadOnlyList<ProcessInstance> Snapshot(IReadOnlyCollection<string> candidateKeys);
}
