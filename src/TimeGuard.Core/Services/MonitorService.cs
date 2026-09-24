using System.Diagnostics;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>
/// Background polling loop. Every 5 seconds it:
///   1. Enumerates running processes
///   2. Accumulates usage time in today's DailyUsage rows
///   3. Tracks time-since-last-break per session
///   4. Calls RulesEngine to get actions
///   5. Fires events so App.xaml.cs can show popups and kill processes
/// </summary>
public sealed class MonitorService : IDisposable, IAsyncDisposable
{
    private readonly DatabaseService _db;
    private readonly RulesEngine     _rules;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleGate = new();
    private readonly IAppLogger? _logger;
    private readonly Func<Dictionary<string, string>> _processSnapshot;
    private Task? _worker;
    private bool _stopped;
    private bool _cancellationDisposed;

    public Task Completion { get { lock (_lifecycleGate) return _worker ?? Task.CompletedTask; } }
    public Exception? LastFault { get; private set; }
    public CancellationToken StoppingToken { get; }

    private AppConfig _config;
    private DailyLog  _log;

    // processName (lowercase) → (sessionDbId, timeSinceBreakMins)
    private readonly Dictionary<string, (int SessionId, double TimeSinceBreak)> _sessions = new();

    public event Action<string, string>? BlockRequested;      // (processName, displayName)
    public event Action<string, string>? WarnRequested;       // (processName, displayName)
    public event Action<string, string, int>? BreakRequested; // (processName, displayName, sessionId)

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const double PollMinutes = 5.0 / 60.0;

    public MonitorService(DatabaseService db, RulesEngine rules, AppConfig config, IAppLogger? logger = null)
        : this(db, rules, config, logger, GetAllWindowedProcessNames) { }

    internal MonitorService(DatabaseService db, RulesEngine rules, AppConfig config,
        IAppLogger? logger, Func<Dictionary<string, string>> processSnapshot)
    {
        _db     = db;
        _rules  = rules;
        _config = config;
        _logger = logger;
        _processSnapshot = processSnapshot;
        StoppingToken = _cts.Token;
        _log    = db.LoadTodayLog();
        db.PurgeOldPassiveSessions(7);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_stopped) throw new ObjectDisposedException(nameof(MonitorService));
            if (_worker is not null) return; // One worker per service lifetime, including after a fault.
            _worker = Task.Run(SuperviseAsync);
            // Observe even if a caller forgets to await Completion. It remains faulted for callers.
            _ = _worker.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task SuperviseAsync()
    {
        _logger.TryWrite("Information", "MonitorStarted");
        try
        {
            try { await RunLoop(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LastFault = ex;
                throw;
            }
            finally
            {
                try { CloseAllSessions(); }
                catch (Exception ex)
                {
                    _logger.TryWrite("Error", "MonitorSessionCleanupFailed", ex);
                    // Preserve a pending worker exception and its original stack trace.
                    // With no worker failure, cleanup itself must fault Completion.
                    if (LastFault is null) throw;
                }
            }
            _logger.TryWrite("Information", "MonitorStopped");
        }
        catch (Exception ex)
        {
            LastFault = ex;
            _logger.TryWrite("Critical", "MonitorFaulted", ex);
            throw;
        }
    }

    public async Task StopAsync()
    {
        Task worker;
        lock (_lifecycleGate)
        {
            _stopped = true;
            if (!_cancellationDisposed) _cts.Cancel();
            worker = _worker ?? Task.CompletedTask;
        }
        try { await worker.ConfigureAwait(false); }
        finally
        {
            lock (_lifecycleGate)
            {
                if (!_cancellationDisposed) _cts.Dispose();
                _cancellationDisposed = true;
            }
        }
    }

    public void ReloadConfig(AppConfig config)
    {
        _config = config;

        // Re-evaluate blocked entries against the new limits; unblock if the limit was raised.
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var entry in _log.Entries.Where(e => e.Blocked))
        {
            var rule = config.Rules.FirstOrDefault(r =>
                r.ProcessName.Equals(entry.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (rule is null) continue;

            if (!rule.HasDailyLimit || entry.UsageMinutes < rule.DailyLimitMinutes)
            {
                entry.Blocked = false;
                // Reset warning so it can fire again as usage approaches the new limit
                if (rule.HasDailyLimit && entry.UsageMinutes < rule.DailyLimitMinutes - 5)
                    entry.WarningSent = false;
                _db.UpsertUsageEntry(today, entry);
            }
        }

        // Unset the overall cap flag if the new cap is higher than current total usage.
        if (_log.OverallCapHit &&
            (config.OverallDailyLimitMinutes == 0 ||
             _log.TotalUsageMinutes < config.OverallDailyLimitMinutes))
        {
            _log.OverallCapHit = false;
        }
    }

    /// <summary>Exposes whether the overall daily cap has been hit (used in tests).</summary>
    public bool IsOverallCapHit => _log.OverallCapHit;

    /// <summary>Called by App.xaml.cs after the break overlay is dismissed.</summary>
    public void OnBreakCompleted(string processName)
    {
        var key = processName.ToLowerInvariant();
        if (_sessions.TryGetValue(key, out var s))
        {
            _db.ResetBreakTimer(s.SessionId);
            _sessions[key] = (s.SessionId, 0);
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        var lastDate = DateOnly.FromDateTime(DateTime.Now);

        while (!ct.IsCancellationRequested)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            if (today != lastDate)
            {
                _log = _db.LoadTodayLog();
                CloseAllSessions();
                lastDate = today;
            }

            var runningNames    = _processSnapshot();
            var ruledRunning    = runningNames.Keys.Where(n => IsRuledProcess(n)).ToList();
            var now             = TimeOnly.FromDateTime(DateTime.Now);

            AccumulateUsage(runningNames);

            var breakTimers = _sessions.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.TimeSinceBreak);
            var actions = _rules.Evaluate(ruledRunning, _log, _config, now, breakTimers);

            foreach (var action in actions)
            {
                var entry = _log.GetOrCreate(action.ProcessName);

                if (action.Kind == RulesEngine.ActionKind.Block)
                {
                    entry.Blocked = true;
                    CloseSession(action.ProcessName);
                    _db.UpsertUsageEntry(today, entry);
                    BlockRequested?.Invoke(action.ProcessName, action.DisplayName);
                }
                else if (action.Kind == RulesEngine.ActionKind.WarnFiveMinutes)
                {
                    entry.WarningSent = true;
                    _db.UpsertUsageEntry(today, entry);
                    WarnRequested?.Invoke(action.ProcessName, action.DisplayName);
                }
                else if (action.Kind == RulesEngine.ActionKind.BreakDue)
                {
                    if (_sessions.TryGetValue(action.ProcessName.ToLowerInvariant(), out var s))
                        BreakRequested?.Invoke(action.ProcessName, action.DisplayName, s.SessionId);
                }
            }

            foreach (var (procName, displayName) in _rules.GetRelaunched(ruledRunning, _log, _config))
                BlockRequested?.Invoke(procName, displayName);

            _log.TotalUsageMinutes = _log.Entries.Sum(e => e.UsageMinutes);
            if (_config.OverallDailyLimitMinutes > 0 &&
                _log.TotalUsageMinutes >= _config.OverallDailyLimitMinutes)
                _log.OverallCapHit = true;

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> GetAllWindowedProcessNames()
    {
        // Returns processName (lowercase) → windowTitle for all visible windowed apps
        var result = new Dictionary<string, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var title = process.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title))
                        result.TryAdd(process.ProcessName.ToLowerInvariant(), title);
                }
                catch (InvalidOperationException) { /* Process exited during enumeration. */ }
                catch (System.ComponentModel.Win32Exception) { /* Inaccessible process. */ }
            }
        }
        return result;
    }

    private bool IsRuledProcess(string processNameLower) =>
        _config.Rules.Any(r => r.Enabled && r.ProcessName.ToLowerInvariant() == processNameLower);

    private void AccumulateUsage(Dictionary<string, string> running)
    {
        var today      = DateOnly.FromDateTime(DateTime.Now);

        // Open new sessions for newly-seen processes
        foreach (var (name, title) in running)
        {
            if (!_sessions.ContainsKey(name))
            {
                var isPassive = !IsRuledProcess(name);
                var sessionId = _db.OpenSession(name, title, isPassive);
                _sessions[name] = (sessionId, 0);
            }
            else
            {
                // Update window title on each tick (keep last seen)
                _db.UpdateSessionTitle(_sessions[name].SessionId, title);
            }
        }

        // Close sessions for processes that stopped
        foreach (var name in _sessions.Keys.ToList())
            if (!running.ContainsKey(name))
                CloseSession(name);

        // Accumulate usage time
        foreach (var (name, _) in running)
        {
            if (_log.IsBlocked(name)) continue;

            var entry = _log.GetOrCreate(name);
            entry.UsageMinutes += PollMinutes;

            if (_sessions.TryGetValue(name, out var s))
            {
                var newBreak = s.TimeSinceBreak + PollMinutes;
                _sessions[name] = (s.SessionId, newBreak);
                if (IsRuledProcess(name))
                    _db.UpdateSession(s.SessionId, newBreak);
            }

            _db.UpsertUsageEntry(today, entry);
        }
    }

    private void CloseSession(string processName)
    {
        if (!_sessions.TryGetValue(processName, out var s)) return;
        _db.CloseSession(s.SessionId, s.TimeSinceBreak);
        _sessions.Remove(processName);
    }

    private void CloseAllSessions()
    {
        foreach (var name in _sessions.Keys.ToList())
            CloseSession(name);
    }

    public void Dispose()
    {
        // Nonblocking cancellation: a UI caller may be servicing a worker event.
        // Await StopAsync/DisposeAsync before releasing dependencies.
        lock (_lifecycleGate)
        {
            _stopped = true;
            if (!_cancellationDisposed) _cts.Cancel();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
