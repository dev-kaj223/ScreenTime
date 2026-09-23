using System.Diagnostics;

namespace TimeGuard.Services;

/// <summary>Test-only ownership receipt. Never find teardown targets by process name.</summary>
public sealed record OwnedProcessIdentity(int Id, long StartTimeUtcTicks, string ProcessName)
{
    public static OwnedProcessIdentity Capture(Process process) =>
        new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName);

    public bool Matches(Process process) => !process.HasExited && process.Id == Id &&
        process.StartTime.ToUniversalTime().Ticks == StartTimeUtcTicks &&
        string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);

    public bool Terminate()
    {
        try
        {
            using var process = Process.GetProcessById(Id);
            _ = process.Handle; // Retain this process handle across validation and Kill (PID reuse).
            if (!Matches(process)) return false;
            process.Kill(); // Never kill a process tree or another same-name instance.
            return process.WaitForExit(5000);
        }
        catch (ArgumentException) { return false; } // Already exited.
        catch (InvalidOperationException) { return false; }
    }
}
