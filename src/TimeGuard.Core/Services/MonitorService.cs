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
    private DowntimeEvaluator _downtime;
    private UsageAccounting _accounting;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _rebase;
    private int _suspended;
    private TimeSpan _nextWait = TimeSpan.FromSeconds(5);
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

    public MonitorService(IStateStore db, RulesEngine rules, AppConfig config, IAppLogger? logger = null,
        IProcessMonitor? processes = null, IProcessTerminator? terminator = null, TimeProvider? time = null)
    {
        _db = db;
        _rules = rules;
        _logger = logger;
        _processes = processes ?? new WindowsProcessMonitor(logger);
        _enforcement = new EnforcementService(terminator ?? new WindowsProcessTerminator(), logger);
        _time = time ?? TimeProvider.System;
        _downtime = new DowntimeEvaluator(_time.LocalTimeZone);
        _accounting = new UsageAccounting(_time, _downtime);
        _rulesConfig = CopyRules(config);
        StoppingToken = _cts.Token;
        _log = db.LoadLog(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
    }

    private static AppRule[] CopyRules(AppConfig config)
    {
        var rules = config.Rules.Select(rule => new AppRule
        {
            ProcessName = ProcessInstance.NormalizeKey(rule.ProcessName), DisplayName = rule.DisplayName,
            Id = rule.Id, Enabled = rule.Enabled, DaySchedules = rule.GetWeekSchedule(),
            BlockedPeriods = rule.BlockedPeriods.Select(p => p with { }).ToList()
        }).ToArray();
        if (rules.Any(r => string.IsNullOrWhiteSpace(r.ProcessName)) ||
            rules.Select(r => r.ProcessName).Distinct().Count() != rules.Length)
            throw new ArgumentException("Each configured application must have one nonempty canonical process key.");
        foreach (var period in rules.SelectMany(r => r.BlockedPeriods)) period.Validate();
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
    public void ReloadConfig(AppConfig config)
    {
        Interlocked.Exchange(ref _pendingConfig, CopyRules(config));
        Wake();
    }

    // Power callbacks publish signals only; the coordinator owns all accounting state.
    public void NotifySuspend() { Interlocked.Exchange(ref _suspended, 1); Interlocked.Exchange(ref _rebase, 1); Wake(); }
    public void NotifyResume() { Interlocked.Exchange(ref _rebase, 1); Interlocked.Exchange(ref _suspended, 0); Wake(); }
    public void NotifyTimeChanged() { Interlocked.Exchange(ref _rebase, 1); Wake(); }
    private void Wake() { if (_wake.CurrentCount == 0) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TickAsync(ct).ConfigureAwait(false);
            await _wake.WaitAsync(_nextWait, ct).ConfigureAwait(false);
        }
    }

    // Deterministic driver for tests; the same gate serializes test ticks and the worker.
    internal async Task TickAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_downtime.Zone.HasSameRules(_time.LocalTimeZone) || _downtime.Zone.Id != _time.LocalTimeZone.Id)
            {
                _downtime = new DowntimeEvaluator(_time.LocalTimeZone);
                _accounting = new UsageAccounting(_time, _downtime);
            }
            var pending = Interlocked.Exchange(ref _pendingConfig, null);
            if (pending is not null) { _rulesConfig = pending; _accounting.Reset(); }
            var configured = _rulesConfig.Where(r => r.Enabled).ToArray();
            var keys = configured.Select(r => r.ProcessName).ToHashSet(StringComparer.Ordinal);
            var instances = _processes.Snapshot(keys).Where(p => keys.Contains(p.AppKey)).Distinct().ToArray();
            var running = instances.Select(p => p.AppKey).ToHashSet(StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            var observationTimestamp = _time.GetTimestamp();
            var today = _downtime.LocalDate(now);
            if (Interlocked.Exchange(ref _rebase, 0) != 0) _accounting.Reset();
            if (Volatile.Read(ref _suspended) != 0) { _accounting.Reset(); _nextWait = PollInterval; return; }
            var logs = new Dictionary<DateOnly, DailyLog> { [_log.Date] = _log };
            DailyLog GetLog(DateOnly date)
            {
                if (!logs.TryGetValue(date, out var log)) logs[date] = log = _db.LoadLog(date);
                return log;
            }
            if (today != _log.Date)
            {
                CloseAllSessions();
                _log = GetLog(today);
            }
            _accounting.Observe(now, instances, configured, GetLog, observationTimestamp);
            // Materialize today's zero bucket even when downtime denies the first launch.
            foreach (var rule in configured) _log.GetOrCreate(rule.ProcessName);
            _db.SaveUsage(logs.Values.Select(log => new DailyLog
            { Date = log.Date, Entries = log.Entries.Where(e => keys.Contains(e.ProcessName)).ToList() }));
            foreach (var name in _sessions.Keys.Where(n => !running.Contains(n)).ToArray()) CloseSession(name);
            foreach (var name in running)
                if (!_sessions.ContainsKey(name)) _sessions[name] = _db.OpenSession(name);

            var decisions = new List<PolicyDecision>();
            foreach (var rule in _rulesConfig)
            {
                var snapshot = PolicySnapshot.Capture(rule, _log, now, running.Contains(rule.ProcessName), _downtime,
                    date => GetLog(date).Entries.FirstOrDefault(e => e.ProcessName == rule.ProcessName)?.QuotaSeconds ?? 0);
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
            foreach (var result in results.Where(r => r.Outcome is TerminationOutcome.Terminated or TerminationOutcome.AlreadyExited))
                _accounting.Forget(result.Target);
            var next = _downtime.MidnightAfter(now);
            foreach (var decision in decisions)
            {
                foreach (var boundary in new[] { decision.DowntimeEnd, decision.NextDowntimeStart })
                    if (boundary > now && boundary < next) next = boundary.Value;
                var rule = configured.FirstOrDefault(r => r.ProcessName == decision.AppKey);
                if (rule is null || !decision.MayContinue || !running.Contains(rule.ProcessName)) continue;
                var limit = rule.GetScheduleForDay(today.DayOfWeek).DailyLimitMinutes * 60L;
                var remaining = limit - _log.GetOrCreate(rule.ProcessName).QuotaSeconds;
                if (limit > 0 && remaining > 0 && now.AddSeconds(remaining) < next) next = now.AddSeconds(remaining);
            }
            var delay = next - _time.GetUtcNow();
            _nextWait = delay < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : delay < PollInterval ? delay : PollInterval;
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
