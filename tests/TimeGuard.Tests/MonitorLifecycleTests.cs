using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class MonitorLifecycleTests
{
    private static MonitorService CreateMonitor(DatabaseService db, RulesEngine rules, AppConfig config,
        IAppLogger? logger = null, Func<Dictionary<string, string>>? snapshot = null) =>
        new(db, rules, config, logger,
            new FakeProcesses(() => (snapshot?.Invoke() ?? []).Keys.Select((name, index) =>
                new ProcessInstance(name, index + 1, 12345, 1)).ToArray()), new FakeTerminator());

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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Supervision_PreservesWorkerFailure_AndReportsSessionCleanupFailure(
        bool failWorker, bool failCleanup)
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var logger = new RecordingLogger();
        var workerFailure = new InvalidOperationException("original worker failure");
        var config = new AppConfig
        {
            Rules = [new AppRule { ProcessName = "helper", DailyLimitMinutes = 5 }]
        };
        var warningHandled = false;
        var monitor = CreateMonitor(db, new RulesEngine(), config, logger,
            () => warningHandled && failWorker ? throw workerFailure : new() { ["helper"] = "owned test helper" });
        monitor.WarnRequested += (_, _) =>
        {
            // The first warning occurs after the real session has been opened.
            // Fail only session closure, leaving the rest of the tick/storage intact.
            if (failCleanup)
            {
                using var connection = new SqliteConnection($"Data Source={profile.Runtime.Paths.DatabasePath}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER FailSessionClose BEFORE UPDATE OF EndTime ON Sessions
                    BEGIN SELECT RAISE(ABORT, 'injected session cleanup failure'); END;
                    """;
                command.ExecuteNonQuery();
            }
            warningHandled = true;
            if (!failWorker) monitor.Dispose(); // Failure is injected at the next process observation, not through UI.
        };
        monitor.Start();
        try
        {
            var error = await Record.ExceptionAsync(() => monitor.Completion.WaitAsync(TimeSpan.FromSeconds(12)));
            if (failWorker || failCleanup)
            {
                Assert.True(monitor.Completion.IsFaulted);
                Assert.Same(error, monitor.LastFault);
                if (failWorker)
                {
                    Assert.Same(workerFailure, error);
                    Assert.Contains(nameof(Supervision_PreservesWorkerFailure_AndReportsSessionCleanupFailure), error!.StackTrace);
                }
                else
                    Assert.IsType<SqliteException>(error);
                Assert.Same(error, Assert.Single(logger.Entries, e => e.Event == "MonitorFaulted").Error);
                Assert.DoesNotContain(logger.Entries, e => e.Event == "MonitorStopped");
            }
            else
            {
                Assert.Null(error);
                Assert.Null(monitor.LastFault);
                Assert.True(monitor.Completion.IsCompletedSuccessfully);
                Assert.Contains(logger.Entries, e => e.Event == "MonitorStopped");
                Assert.DoesNotContain(logger.Entries, e => e.Level is "Error" or "Critical");
                Assert.Contains("T", Assert.Single(db.LoadSessionsForDay(DateOnly.FromDateTime(DateTime.Today))).EndTime);
            }

            if (failCleanup)
            {
                var cleanup = Assert.Single(logger.Entries, e => e.Event == "MonitorSessionCleanupFailed");
                Assert.Equal("Error", cleanup.Level);
                Assert.Contains("injected session cleanup failure", Assert.IsType<SqliteException>(cleanup.Error).Message);
                if (failWorker) Assert.NotSame(error, cleanup.Error);
                else Assert.Same(error, cleanup.Error);
            }
            else
                Assert.DoesNotContain(logger.Entries, e => e.Event == "MonitorSessionCleanupFailed");
        }
        finally
        {
            // StopAsync also awaits a faulted worker; observe it while releasing its resources.
            await Record.ExceptionAsync(() => monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task StartStop_CancellationIsNormal_AndDuplicateStartsShareOneLoop()
    {
        using var profile = new TempProfile();
        var logger = new RecordingLogger();
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var monitor = CreateMonitor(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig(), logger,
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
        var monitor = CreateMonitor(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig(), logger,
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
        await using var monitor = CreateMonitor(db, new RulesEngine(), new AppConfig(), new ThrowingLogger(),
            () => { sampled.TrySetResult(); return []; });
        monitor.Start();
        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
        var failing = CreateMonitor(db, new RulesEngine(), new AppConfig(), new ThrowingLogger(),
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
        await using var monitor = CreateMonitor(db, new RulesEngine(), new AppConfig { Rules = [new() { ProcessName = "test-process" }] }, null, () =>
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
        await using var monitor = CreateMonitor(db, new RulesEngine(), config, null,
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
        await using var monitor = CreateMonitor(new DatabaseService(profile.Runtime.Paths), new RulesEngine(), new AppConfig());
        await monitor.StopAsync();
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }
}
