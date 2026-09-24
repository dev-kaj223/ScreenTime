using TimeGuard.Models;
using TimeGuard.Services;

namespace TimeGuard.Tests;

internal sealed class FakeProcesses(Func<IReadOnlyList<ProcessInstance>> snapshot) : IProcessMonitor
{
    public IReadOnlyList<ProcessInstance> Snapshot(IReadOnlyCollection<string> keys) => snapshot();
    public bool ConfirmedExited(ProcessInstance instance) => !snapshot().Contains(instance);
}
internal sealed class FakeTerminator : IProcessTerminator
{
    public List<ProcessInstance> Targets { get; } = [];
    public TerminationOutcome Outcome { get; set; } = TerminationOutcome.Terminated;
    public Task<TerminationResult> TerminateAsync(ProcessInstance instance, CancellationToken cancellationToken)
    {
        Targets.Add(instance);
        return Task.FromResult(new TerminationResult(instance, Outcome));
    }
}
internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public long? Timestamp { get; set; }
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public override long GetTimestamp() => Timestamp ?? Now.UtcTicks;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => Zone;
}
