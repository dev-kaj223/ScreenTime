using Microsoft.Win32;
using TimeGuard.Services;

namespace TimeGuard.Helpers;

/// <summary>Power events only queue a rebase/wakeup; there is no second policy timer.</summary>
public sealed class PowerSessionHelper : IDisposable
{
    private readonly MonitorService _monitor;
    public PowerSessionHelper(MonitorService monitor)
    {
        _monitor = monitor;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _monitor.NotifySuspend();
        else if (e.Mode == PowerModes.Resume) _monitor.NotifyResume();
    }
    private void OnTimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        _monitor.NotifyTimeChanged();
    }
    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
    }
}
