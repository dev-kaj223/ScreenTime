using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TimeGuard.Services;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

public class TestIsolationTests
{
    [Fact]
    public async Task HeadlessHelper_IsObservedAndExactInstanceTerminated_OtherSameNameSurvives()
    {
        using var fixture = new SeededAppFixture();
        using var other = new SeededAppFixture();
        var target = fixture.LaunchHelper(headless: true);
        var unrelated = other.LaunchHelper(headless: true);
        var scope = new TestProcessScope(fixture.Runtime.Paths);
        var monitor = new WindowsProcessMonitor(isAllowedTarget: scope.Contains);
        var instance = Assert.Single(monitor.Snapshot(["ScreenTime.TestProcess.exe"]));
        Assert.Equal(target.Id, instance.ProcessId);
        using (var process = Process.GetProcessById(target.Id)) Assert.Equal("", process.MainWindowTitle);
        var terminator = new WindowsProcessTerminator(scope.Contains);
        var mismatch = new TimeGuard.Models.ProcessInstance(instance.AppKey, instance.ProcessId,
            instance.StartTimeUtcTicks + 1, instance.SessionId);
        Assert.Equal(TerminationOutcome.IdentityMismatch, (await terminator.TerminateAsync(mismatch, default)).Outcome);
        Assert.True(IsAlive(target));
        Assert.Equal(TerminationOutcome.Terminated, (await terminator.TerminateAsync(instance, default)).Outcome);
        Assert.False(IsAlive(target));
        Assert.True(IsAlive(unrelated));
        Assert.Equal(TerminationOutcome.AlreadyExited, (await terminator.TerminateAsync(instance, default)).Outcome);
    }

    [Fact]
    public void Shutdown_WithModalSettingsPrompt_StopsCleanly()
    {
        using var fixture = new SeededAppFixture();
        fixture.RequestSettings();
        _ = fixture.App.WaitForWindow(fixture.Automation, "Parent Access");
        using var process = Process.GetProcessById(fixture.App.ProcessId);
        using var signal = EventWaitHandle.OpenExisting(fixture.Runtime.StopEventName);
        signal.Set();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, fixture.App.ExitCode);
    }

    [Fact]
    public void FixtureTeardown_LeavesOtherAppAndSameNameHelperAlive()
    {
        using var other = new SeededAppFixture();
        var otherHelper = other.LaunchHelper();
        using var owned = new SeededAppFixture();
        var ownedHelper = owned.LaunchHelper();
        var ownedRoot = owned.Runtime.Paths.Root;
        var ownedAppId = owned.App.ProcessId;
        Assert.NotEqual(other.Runtime.MutexName, owned.Runtime.MutexName);
        owned.Dispose();
        Assert.False(Directory.Exists(ownedRoot));
        Assert.False(IsAlive(ownedHelper));
        Assert.True(IsAlive(otherHelper));
        using var otherApp = Process.GetProcessById(other.App.ProcessId);
        Assert.False(otherApp.HasExited);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(ownedAppId));
    }

    [Fact]
    public void IdentityMismatch_NeverTerminatesProcess()
    {
        using var fixture = new SeededAppFixture();
        var helper = fixture.LaunchHelper();
        Assert.False((helper with { StartTimeUtcTicks = helper.StartTimeUtcTicks + 1 }).Terminate());
        Assert.False((helper with { ProcessName = "different-name" }).Terminate());
        Assert.True(IsAlive(helper));
    }

    [Fact]
    public void PopupEnforcement_LeavesUnownedSameNameHelperAlive()
    {
        using var other = new SeededAppFixture();
        var unowned = other.LaunchHelper();
        using var fixture = new PopupTestFixture();
        fixture.EnsureHelperRunning();
        Assert.True(SpinWait.SpinUntil(fixture.HelperExited, TimeSpan.FromSeconds(20)));
        Assert.True(IsAlive(unowned));
    }

    [Fact]
    public void MonitorFault_LogsAndExitsNonzero()
    {
        using var fixture = new SelectedHelperFixture();
        using var process = Process.GetProcessById(fixture.App.ProcessId);
        // Fail the next configured helper's session write, after the monitor has started.
        using (var connection = new SqliteConnection($"Data Source={fixture.Runtime.Paths.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE Sessions";
            command.ExecuteNonQuery();
        }
        fixture.LaunchHelper();
        Assert.True(process.WaitForExit(15000));
        Assert.NotEqual(0, fixture.App.ExitCode);
        var log = File.ReadAllText(fixture.Runtime.Paths.LogPath);
        Assert.Contains("MonitorFaulted", log);
        Assert.Contains("SqliteException", log);
    }

    [Fact]
    public void NormalShutdown_IsLoggedWithoutCrash()
    {
        using var fixture = new SeededAppFixture();
        using var process = Process.GetProcessById(fixture.App.ProcessId);
        using var signal = EventWaitHandle.OpenExisting(fixture.Runtime.StopEventName);
        signal.Set();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, fixture.App.ExitCode);
        var log = File.ReadAllText(fixture.Runtime.Paths.LogPath);
        Assert.Contains("MonitorStopped", log);
        Assert.Contains("ApplicationStopped", log);
        Assert.DoesNotContain("MonitorFaulted", log);
    }

    private sealed class SelectedHelperFixture : SeededAppFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            OpenDatabase().SaveRule(new TimeGuard.Models.AppRule { ProcessName = "screentime.testprocess" });
        }
    }
    private static bool IsAlive(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            return identity.Matches(process);
        }
        catch (ArgumentException) { return false; }
    }
}
