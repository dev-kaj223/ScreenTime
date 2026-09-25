using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TimeGuard.Helpers;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

public class Phase7ReliabilityTests
{
    private static ProcessStartInfo StartInfo(string profile)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "App", "ScreenTime.exe")) { UseShellExecute = false };
        info.ArgumentList.Add("--test-profile"); info.ArgumentList.Add(profile);
        info.Environment.Remove("TIMEGUARD_TEST_DB"); return info;
    }

    [Fact]
    public void DuplicateLaunch_ExitsCleanly_OriginalMonitorAndProfileRemainActive()
    {
        using var fixture = new SeededAppFixture();
        using var original = Process.GetProcessById(fixture.App.ProcessId);
        using var duplicate = Process.Start(StartInfo(fixture.Runtime.Paths.Root))!;
        var owned = OwnedProcessIdentity.Capture(duplicate);
        try
        {
            Assert.True(duplicate.WaitForExit(10000)); Assert.Equal(0, duplicate.ExitCode);
            Assert.False(original.HasExited);
            Assert.Single(File.ReadLines(fixture.Runtime.Paths.LogPath).Where(line => line.Contains("MonitorStarted")));
            using var stop = EventWaitHandle.OpenExisting(fixture.Runtime.StopEventName); stop.Set();
            Assert.True(original.WaitForExit(10000)); Assert.Equal(0, fixture.App.ExitCode);
        }
        finally { owned.Terminate(); }
    }

    [Fact]
    public void CorruptProfile_StartupExitsNonzero_PreservesDatabase_AndLogsCause()
    {
        var runtime = RuntimeOptions.Test(Path.Combine(Path.GetTempPath(), "ScreenTime-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runtime.Paths.Root);
        var corrupt = "preserve corrupt database evidence";
        File.WriteAllText(runtime.Paths.DatabasePath, corrupt);
        try
        {
            using var process = Process.Start(StartInfo(runtime.Paths.Root))!;
            var owned = OwnedProcessIdentity.Capture(process);
            try
            {
                Assert.True(process.WaitForExit(10000)); Assert.NotEqual(0, process.ExitCode);
                Assert.Equal(corrupt, File.ReadAllText(runtime.Paths.DatabasePath));
                var log = File.ReadAllText(runtime.Paths.LogPath);
                Assert.Contains("ApplicationStartupFailed", log); Assert.Contains("SqliteException", log);
            }
            finally { owned.Terminate(); }
        }
        finally { Directory.Delete(runtime.Paths.Root, true); }
    }

    [Theory] [InlineData("invalid", "invalid")] [InlineData("", "")]
    [InlineData("AA==", "AA==")]
    public void MalformedCredentials_FailClosedWithoutThrowing(string hash, string salt) =>
        Assert.False(PasswordHelper.Verify("password", hash, salt));

    [Fact]
    public async Task IsolatedIdleApplication_ResourceSamples_AndCleanShutdown()
    {
        using var fixture = new IdleSelectedAppFixture();
        using var process = Process.GetProcessById(fixture.App.ProcessId);
        var duration = int.TryParse(Environment.GetEnvironmentVariable("SCREENTIME_PHASE7_SOAK_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 10, 3600) : 10;
        var samples = new List<ResourceSample>(); var elapsed = Stopwatch.StartNew();
        void Sample()
        {
            process.Refresh(); Assert.False(process.HasExited);
            Assert.True(GetProcessIoCounters(process.Handle, out var io));
            samples.Add(new(elapsed.Elapsed.TotalSeconds, process.TotalProcessorTime.TotalSeconds,
                process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount, io.ReadTransferCount, io.WriteTransferCount));
        }
        Sample();
        do { await Task.Delay(TimeSpan.FromSeconds(5)); Sample(); } while (elapsed.Elapsed.TotalSeconds < duration);
        using var stop = EventWaitHandle.OpenExisting(fixture.Runtime.StopEventName); stop.Set();
        Assert.True(process.WaitForExit(10000)); Assert.Equal(0, fixture.App.ExitCode);
        Assert.DoesNotContain("MonitorFaulted", File.ReadAllText(fixture.Runtime.Paths.LogPath));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "phase7-idle-resources.json"),
            JsonSerializer.Serialize(new { Configuration = "Release; isolated Test profile; one configured helper absent; tray idle; status closed", DurationSeconds = elapsed.Elapsed.TotalSeconds,
                CpuPercentOfOneCore = 100 * (samples[^1].CpuSeconds - samples[0].CpuSeconds) / (samples[^1].ElapsedSeconds - samples[0].ElapsedSeconds),
                Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class IdleSelectedAppFixture : SeededAppFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            OpenDatabase().SaveRule(new() { ProcessName = "screentime.testprocess", DailyLimitMinutes = 60 });
        }
    }

    private sealed record ResourceSample(double ElapsedSeconds, double CpuSeconds, long PrivateBytes, long WorkingSetBytes,
        int Handles, ulong ReadBytes, ulong WriteBytes);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
}
