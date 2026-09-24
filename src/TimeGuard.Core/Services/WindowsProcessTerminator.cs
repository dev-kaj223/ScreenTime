using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TimeGuard.Models;

namespace TimeGuard.Services;

public sealed class WindowsProcessTerminator : IProcessTerminator
{
    private readonly Func<int, IProcessTarget> _open;
    private readonly Func<ProcessInstance, bool>? _isAllowedTarget;
    private readonly int _sessionId;

    public WindowsProcessTerminator(Func<ProcessInstance, bool>? isAllowedTarget = null)
    {
        _open = id => new WindowsProcessTarget(id);
        _isAllowedTarget = isAllowedTarget;
        using var current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
    }

    internal WindowsProcessTerminator(Func<int, IProcessTarget> open, int sessionId)
    {
        _open = open;
        _sessionId = sessionId;
    }

    public async Task<TerminationResult> TerminateAsync(ProcessInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instance.ProcessId <= 0 || instance.StartTimeUtcTicks <= 0 ||
                instance.SessionId != _sessionId || !(_isAllowedTarget?.Invoke(instance) ?? true))
                return new(instance, TerminationOutcome.IdentityMismatch);
            using var target = _open(instance.ProcessId);
            if (target.HasExited) return new(instance, TerminationOutcome.AlreadyExited);
            if (target.Identity != instance) return new(instance, TerminationOutcome.IdentityMismatch);
            if (!target.IsCurrentUser) return new(instance, TerminationOutcome.AccessDenied);
            // The adapter retains the same OS handle across identity/owner validation and Kill.
            target.Kill();
            using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirmation.CancelAfter(TimeSpan.FromSeconds(2));
            try { await target.WaitForExitAsync(confirmation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(instance, TerminationOutcome.TimedOut);
            }
            return new(instance, TerminationOutcome.Terminated);
        }
        catch (ArgumentException) { return new(instance, TerminationOutcome.AlreadyExited); }
        catch (InvalidOperationException) { return new(instance, TerminationOutcome.AlreadyExited); }
        catch (Win32Exception ex) { return new(instance, ex.NativeErrorCode == 5 ? TerminationOutcome.AccessDenied : TerminationOutcome.Failed, ex); }
        catch (UnauthorizedAccessException ex) { return new(instance, TerminationOutcome.AccessDenied, ex); }
        catch (TimeoutException ex) { return new(instance, TerminationOutcome.TimedOut, ex); }
    }
}

// Narrow handle seam exercises PID reuse/access races without killing real applications.
internal interface IProcessTarget : IDisposable
{
    bool HasExited { get; }
    ProcessInstance Identity { get; }
    bool IsCurrentUser { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsProcessTarget : IProcessTarget
{
    private readonly Process _process;
    public WindowsProcessTarget(int id)
    {
        _process = Process.GetProcessById(id);
        try { _ = _process.Handle; }
        catch { _process.Dispose(); throw; }
    }
    public bool HasExited => _process.HasExited;
    public ProcessInstance Identity => new(_process.ProcessName, _process.Id,
        _process.StartTime.ToUniversalTime().Ticks, _process.SessionId);
    public bool IsCurrentUser
    {
        get
        {
            if (!OpenProcessToken(_process.Handle, 0x0008 /* TOKEN_QUERY */, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                return owner.User is not null && owner.User == current.User;
        }
    }
    public void Kill() => _process.Kill(); // No process tree, launcher or name-wide fallback.
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Dispose() => _process.Dispose();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint access, out SafeAccessTokenHandle token);
}
