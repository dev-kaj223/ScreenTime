using System.Collections.Concurrent;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class MonitorLifecycleTests
{
    private sealed class RecordingLogger : IAppLogger
    {
        public ConcurrentQueue<(string Level, string Event, Exception? Error)> Entries { get; } = new();
        public void Write(string level, string eventName, Exception? exception = null) =>
            Entries.Enqueue((level, eventName, exception));
    }

    private sealed class ThrowingLogger : IAppLogger
    {
        public void Write(string level, string eventName, Exception? exception = null) => throw new IOException("disk unavailable");
    }

    [Fact]
    public async Task StartStop_CancellationIsNormal_AndDuplicateStartsShareOneLoop()
    {
        using var profile = new TempProfile();
        var logger = new RecordingLogger();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var monitor = new MonitorService(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig(), logger,
            () => { Interlocked.Increment(ref calls); sampled.TrySetResult(); return []; });
        await using (monitor)
        {
            Parallel.For(0, 20, _ => monitor.Start());
            var worker = monitor.Completion;
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            monitor.Start();
            Assert.Same(worker, monitor.Completion);
            await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await monitor.StopAsync();
            Assert.True(worker.IsCompletedSuccessfully);
            Assert.Null(monitor.LastFault);
            Assert.Equal(1, calls);
            Assert.DoesNotContain(logger.Entries, e => e.Level is "Error" or "Critical");
            Assert.Contains(logger.Entries, e => e.Event == "MonitorStopped");
            Assert.Throws<ObjectDisposedException>(monitor.Start);
        }
    }

    [Fact]
    public async Task WorkerFault_IsReportedAndObservable_WithoutRestartingOnDuplicateStart()
    {
        using var profile = new TempProfile();
        var logger = new RecordingLogger();
        var failure = new InvalidOperationException("snapshot failure");
        var monitor = new MonitorService(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig(), logger,
            () => throw failure);
        monitor.Start();
        var worker = monitor.Completion;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => worker));
        Assert.Same(failure, monitor.LastFault);
        Assert.Contains(logger.Entries, e => e.Event == "MonitorFaulted" && e.Error == failure);
        monitor.Start();
        Assert.Same(worker, monitor.Completion);
        await Assert.ThrowsAsync<InvalidOperationException>(monitor.StopAsync);
    }

    [Fact]
    public async Task LoggerFailure_CannotStopMonitoringOrHideOriginalFault()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new MonitorService(db, new RulesEngine(), new AppConfig(), new ThrowingLogger(),
            () => { sampled.TrySetResult(); return []; });
        monitor.Start();
        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
        var failing = new MonitorService(db, new RulesEngine(), new AppConfig(), new ThrowingLogger(),
            () => throw new InvalidOperationException("original failure"));
        failing.Start();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Completion);
        Assert.Equal("original failure", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(failing.StopAsync);
    }

    [Fact]
    public async Task Stop_WaitsForActiveTick_ThenClosesSessions()
    {
        using var profile = new TempProfile();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = new DatabaseService(profile.Runtime.Paths);
        await using var monitor = new MonitorService(db, new RulesEngine(), new AppConfig(), null, () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new() { ["test-process"] = "test window" };
        });
        monitor.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = monitor.StopAsync();
        Assert.False(stopping.IsCompleted);
        release.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(db.LoadSessionsForDay(DateOnly.FromDateTime(DateTime.Today)));
        Assert.Contains("T", db.LoadSessionsForDay(DateOnly.FromDateTime(DateTime.Today))[0].EndTime);
    }

    [Fact]
    public async Task Cancellation_ReleasesAnEventWaitingForUi()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new AppConfig { Rules = [new AppRule { ProcessName = "helper", DailyLimitMinutes = 1 }] };
        db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Today), new UsageEntry { ProcessName = "helper", UsageMinutes = 2 });
        await using var monitor = new MonitorService(db, new RulesEngine(), config, null,
            () => new() { ["helper"] = "owned test helper" });
        monitor.BlockRequested += (_, _) =>
        {
            entered.TrySetResult();
            monitor.StoppingToken.WaitHandle.WaitOne();
            monitor.StoppingToken.ThrowIfCancellationRequested();
        };
        monitor.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopBeforeStart_IsSafeAndPreventsLaterStart()
    {
        using var profile = new TempProfile();
        await using var monitor = new MonitorService(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig());
        await monitor.StopAsync();
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }
}
