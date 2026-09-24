using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>One serialized owner: observe, evaluate, persist, enforce, then notify.</summary>
public sealed class MonitorService : IDisposable, IAsyncDisposable
{
    private readonly IStateStore _db;
    private readonly RulesEngine _rules;
    private readonly IProcessMonitor _processes;
    private readonly EnforcementService _enforcement;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly IAppLogger? _logger;
    private Task? _worker;
    private bool _stopped;
    private bool _cancellationDisposed;
    private AppRule[] _rulesConfig;
    private AppRule[]? _pendingConfig;
    private DailyLog _log;
    private readonly Dictionary<string, int> _sessions = new();
    private IReadOnlyList<PolicyDecision> _decisions = Array.Empty<PolicyDecision>();
    private IReadOnlyList<TerminationResult> _enforcementResults = Array.Empty<TerminationResult>();

    public Task Completion { get { lock (_lifecycleGate) return _worker ?? Task.CompletedTask; } }
    public Exception? LastFault { get; private set; }
    public CancellationToken StoppingToken { get; }
    public IReadOnlyList<PolicyDecision> Decisions => Volatile.Read(ref _decisions);
    public IReadOnlyList<TerminationResult> EnforcementResults => Volatile.Read(ref _enforcementResults);
    public event Action<string, string>? BlockRequested;
    public event Action<string, string>? WarnRequested;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const double PollMinutes = 5.0 / 60.0;

    public MonitorService(IStateStore db, RulesEngine rules, AppConfig config, IAppLogger? logger = null,
        IProcessMonitor? processes = null, IProcessTerminator? terminator = null, TimeProvider? time = null)
    {
        _db = db;
        _rules = rules;
        _logger = logger;
        _processes = processes ?? new WindowsProcessMonitor(logger);
        _enforcement = new EnforcementService(terminator ?? new WindowsProcessTerminator(), logger);
        _time = time ?? TimeProvider.System;
        _rulesConfig = CopyRules(config);
        StoppingToken = _cts.Token;
        _log = db.LoadLog(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
    }

    private static AppRule[] CopyRules(AppConfig config)
    {
        var rules = config.Rules.Select(rule => new AppRule
        {
            ProcessName = ProcessInstance.NormalizeKey(rule.ProcessName), DisplayName = rule.DisplayName,
            Enabled = rule.Enabled, DaySchedules = rule.GetWeekSchedule()
        }).ToArray();
        if (rules.Any(r => string.IsNullOrWhiteSpace(r.ProcessName)) ||
            rules.Select(r => r.ProcessName).Distinct().Count() != rules.Length)
            throw new ArgumentException("Each configured application must have one nonempty canonical process key.");
        return rules;
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
        try
        {
            await worker.ConfigureAwait(false);
            await _tickGate.WaitAsync().ConfigureAwait(false);
            try { CloseAllSessions(); }
            finally { _tickGate.Release(); }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (!_cancellationDisposed) _cts.Dispose();
                _cancellationDisposed = true;
            }
        }
    }

    // UI callers only publish a private copy; no usage/session state is touched here.
    public void ReloadConfig(AppConfig config) => Interlocked.Exchange(ref _pendingConfig, CopyRules(config));

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TickAsync(ct).ConfigureAwait(false);
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    // Deterministic driver for tests; the same gate serializes test ticks and the worker.
    internal async Task TickAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetLocalNow().DateTime;
            var today = DateOnly.FromDateTime(now);
            if (today != _log.Date)
            {
                CloseAllSessions();
                _log = _db.LoadLog(today);
            }
            var pending = Interlocked.Exchange(ref _pendingConfig, null);
            if (pending is not null) _rulesConfig = pending;
            var configured = _rulesConfig.Where(r => r.Enabled).ToArray();
            var keys = configured.Select(r => r.ProcessName).ToHashSet(StringComparer.Ordinal);
            var instances = _processes.Snapshot(keys).Where(p => keys.Contains(p.AppKey)).Distinct().ToArray();
            var running = instances.Select(p => p.AppKey).ToHashSet(StringComparer.Ordinal);
            foreach (var name in _sessions.Keys.Where(n => !running.Contains(n)).ToArray()) CloseSession(name);
            foreach (var name in running)
                if (!_sessions.ContainsKey(name)) _sessions[name] = _db.OpenSession(name);

            var decisions = new List<PolicyDecision>();
            foreach (var rule in _rulesConfig)
            {
                var snapshot = PolicySnapshot.Capture(rule, _log, TimeOnly.FromDateTime(now), running.Contains(rule.ProcessName));
                if (pending is not null && (snapshot.DailyLimitMinutes == 0 || snapshot.DailyLimitMinutes - snapshot.UsageMinutes > 5))
                {
                    var existing = _log.Entries.FirstOrDefault(e => e.ProcessName == rule.ProcessName);
                    if (existing?.WarningSent == true)
                    {
                        existing.WarningSent = false;
                        _db.UpsertUsageEntry(today, existing);
                        snapshot = snapshot with { WarningSent = false };
                    }
                }
                var decision = _rules.Evaluate(snapshot);
                if (rule.Enabled && snapshot.IsRunning && decision.MayContinue)
                {
                    // Preserve minute storage and five-second sampling for Phase 2. Denied
                    // launches are evaluated first and cannot spend the next permitted quota.
                    var entry = _log.GetOrCreate(rule.ProcessName);
                    entry.UsageMinutes += PollMinutes;
                    _db.UpsertUsageEntry(today, entry);
                    snapshot = snapshot with { UsageMinutes = entry.UsageMinutes };
                    decision = _rules.Evaluate(snapshot);
                }
                if (decision.WarnFiveMinutes)
                {
                    var entry = _log.GetOrCreate(rule.ProcessName);
                    entry.WarningSent = true;
                    _db.UpsertUsageEntry(today, entry);
                }
                decisions.Add(decision);
            }
            Volatile.Write(ref _decisions, decisions.AsReadOnly());
            var results = new List<TerminationResult>();
            foreach (var decision in decisions.Where(d => d.TerminationRequired))
                foreach (var instance in instances.Where(p => p.AppKey == decision.AppKey))
                    results.Add(await _enforcement.EnforceAsync(decision, instance, ct).ConfigureAwait(false));
            Volatile.Write(ref _enforcementResults, results.AsReadOnly());
            // All targets are enforced before any UI subscriber is invoked. WPF enqueues
            // asynchronously; subscriber exceptions are diagnostics, never worker failures.
            foreach (var decision in decisions)
            {
                if (decision.TerminationRequired) Notify(BlockRequested, decision);
                else if (decision.WarnFiveMinutes) Notify(WarnRequested, decision);
            }
        }
        finally { _tickGate.Release(); }
    }

    private void Notify(Action<string, string>? handlers, PolicyDecision decision)
    {
        if (handlers is null) return;
        foreach (Action<string, string> handler in handlers.GetInvocationList())
        {
            try { handler(decision.AppKey, decision.DisplayName); }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.TryWrite("Error", "NotificationFailed", ex); }
        }
    }

    private void CloseSession(string name)
    {
        if (!_sessions.TryGetValue(name, out var session)) return;
        _db.CloseSession(session);
        _sessions.Remove(name);
    }

    private void CloseAllSessions()
    {
        foreach (var name in _sessions.Keys.ToArray()) CloseSession(name);
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
