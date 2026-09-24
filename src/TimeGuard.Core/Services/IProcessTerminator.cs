using TimeGuard.Models;

namespace TimeGuard.Services;

public enum TerminationOutcome { Terminated, AlreadyExited, IdentityMismatch, AccessDenied, TimedOut, Failed, NotRequested }

public sealed record TerminationResult(ProcessInstance Target, TerminationOutcome Outcome, Exception? Error = null)
{
    public bool Succeeded => Outcome is TerminationOutcome.Terminated or TerminationOutcome.AlreadyExited;
}

public interface IProcessTerminator
{
    Task<TerminationResult> TerminateAsync(ProcessInstance instance, CancellationToken cancellationToken);
}
