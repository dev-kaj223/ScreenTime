using System.ComponentModel;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class EnforcementServiceTests
{
    private static readonly ProcessInstance Instance = new("helper", 42, 100, 1);
    private sealed class Target : IProcessTarget
    {
        public bool HasExited { get; set; }
        public ProcessInstance Identity { get; set; } = Instance;
        public bool IsCurrentUser { get; set; } = true;
        public int Kills { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? KillFailure { get; set; }
        public void Kill() { if (KillFailure is not null) throw KillFailure; Kills++; }
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ExactInstanceOnly_IsOpenedAndTerminatedWithoutUi()
    {
        var target = new Target();
        var other = new Target { Identity = new("helper", 43, 100, 1) };
        var terminator = new WindowsProcessTerminator(id => { Assert.Equal(42, id); return target; }, 1);
        var service = new EnforcementService(terminator);
        var decision = new RulesEngine().Evaluate(new("helper", "Helper", true, new(2026, 9, 23), 1, 120, false, true, new(false, null, null), null));
        Assert.Equal(TerminationOutcome.Terminated, (await service.EnforceAsync(decision, Instance)).Outcome);
        Assert.Equal(1, target.Kills);
        Assert.Equal(0, other.Kills);
        Assert.True(target.Disposed);
    }

    [Theory]
    [InlineData("helper", 42, 101, 1)] // PID reuse / creation mismatch.
    [InlineData("different", 42, 100, 1)]
    [InlineData("helper", 43, 100, 1)]
    [InlineData("helper", 42, 100, 2)]
    public async Task IdentityMismatch_RejectsKill(string name, int id, long creation, int session)
    {
        var target = new Target { Identity = new(name, id, creation, session) };
        var result = await new WindowsProcessTerminator(_ => target, 1).TerminateAsync(Instance, default);
        Assert.Equal(TerminationOutcome.IdentityMismatch, result.Outcome);
        Assert.Equal(0, target.Kills);
        Assert.True(target.Disposed);
    }

    [Fact]
    public async Task AnotherUser_IsNeverTerminated()
    {
        var target = new Target { IsCurrentUser = false };
        Assert.Equal(TerminationOutcome.AccessDenied,
            (await new WindowsProcessTerminator(_ => target, 1).TerminateAsync(Instance, default)).Outcome);
        Assert.Equal(0, target.Kills);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task VanishedProcess_IsSafe(bool vanishedBeforeOpen)
    {
        var target = new Target { HasExited = true };
        var result = await new WindowsProcessTerminator(_ => vanishedBeforeOpen ? throw new ArgumentException() : target, 1)
            .TerminateAsync(Instance, default);
        Assert.Equal(TerminationOutcome.AlreadyExited, result.Outcome);
        Assert.Equal(0, target.Kills);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InaccessibleProcess_ReturnsExplicitFailure_NoBroadFallback(bool failsAtOpen)
    {
        var target = new Target { KillFailure = new Win32Exception(5) };
        var result = await new WindowsProcessTerminator(_ => failsAtOpen ? throw new Win32Exception(5) : target, 1)
            .TerminateAsync(Instance, default);
        Assert.Equal(TerminationOutcome.AccessDenied, result.Outcome);
        Assert.IsType<Win32Exception>(result.Error);
        Assert.Equal(0, target.Kills);
    }

    [Fact]
    public async Task AllowedDecisionOrWrongApp_DoesNotInvokeTerminator()
    {
        var fake = new FakeTerminator();
        var service = new EnforcementService(fake);
        var allowed = new RulesEngine().Evaluate(new("helper", "Helper", true, new(2026, 9, 23), 60, 0, false, true, new(false, null, null), null));
        Assert.Equal(TerminationOutcome.NotRequested, (await service.EnforceAsync(allowed, Instance)).Outcome);
        Assert.Equal(TerminationOutcome.NotRequested,
            (await service.EnforceAsync(allowed with { MayContinue = false, TerminationRequired = true, AppKey = "other" }, Instance)).Outcome);
        Assert.Empty(fake.Targets);
    }
}
