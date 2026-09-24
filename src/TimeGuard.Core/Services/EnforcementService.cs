using TimeGuard.Models;

namespace TimeGuard.Services;

public sealed class EnforcementService(IProcessTerminator terminator, IAppLogger? logger = null)
{
    public async Task<TerminationResult> EnforceAsync(PolicyDecision decision, ProcessInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (!decision.TerminationRequired || decision.MayContinue || decision.AppKey != instance.AppKey)
            return new(instance, TerminationOutcome.NotRequested);
        TerminationResult result;
        try { result = await terminator.TerminateAsync(instance, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { result = new(instance, TerminationOutcome.Failed, ex); }
        logger.TryWrite(result.Succeeded ? "Information" : "Error", "Enforcement" + result.Outcome, result.Error);
        return result;
    }
}
