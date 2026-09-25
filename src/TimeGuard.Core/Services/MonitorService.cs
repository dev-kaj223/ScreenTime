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
    private long _configurationRevision;
    private DailyLog _log;
    private IReadOnlyList<GraceEpisode> _grace;
    internal TimeSpan GraceDurationForTesting { get; init; } = GraceEpisode.DefaultDuration;
    public IReadOnlyList<GraceEpisode> GraceEpisodes => Volatile.Read(ref _grace);
    private readonly Dictionary<string, int> _sessions = new();
    private IReadOnlyList<PolicyDecision> _decisions = Array.Empty<PolicyDecision>();
    private IReadOnlyList<TerminationResult> _enforcementResults = Array.Empty<TerminationResult>();
    private readonly NotificationOutbox _notices;
    internal Task NotificationWorkForTesting => _notices.Completion;
    private readonly HashSet<string> _noticeAttempts = [];
    private readonly Dictionary<string, DateTimeOffset> _blockedNotices = [];
    private IReadOnlyList<NotificationRequest> _notificationFacts = Array.Empty<NotificationRequest>();
    public IReadOnlyList<NotificationRequest> NotificationFacts => Volatile.Read(ref _notificationFacts);

    // Pull-only bounded mailbox: the enforcement worker never invokes presentation code.
    public bool TryReadNotification(out NotificationRequest? request)
    {
        while (_notices.TryRead(out var next))
        {
            if (next is null || !IsNotificationCurrent(next)) continue;
            request = next;
            return true;
        }
        request = null;
        return false;
    }

    public bool IsNotificationCurrent(NotificationRequest request) =>
        Volatile.Read(ref _pendingConfig) is null && request.ConfigurationRevision == Interlocked.Read(ref _configurationRevision) &&
        NotificationPolicy.IsCurrent(request, Decisions.FirstOrDefault(d => d.AppKey == request.AppKey), _time.GetUtcNow()) &&
        (request.Kind == NotificationKind.Blocked || NotificationFacts.Any(n => n.ReceiptKey == request.ReceiptKey));

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
        _grace = db.LoadGraceEpisodes();
        _notices = new NotificationOutbox(db, IsNotificationCurrent, logger);
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
            await _notices.StopAsync().ConfigureAwait(false);
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
            if (pending is not null) { Interlocked.Increment(ref _configurationRevision); _rulesConfig = pending; _accounting.Reset(); }
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
            var episodes = _grace.ToList();
            GraceEpisode? Grant(QuotaCrossing crossing)
            {
                if (episodes.Any(e => e.AppKey == crossing.AppKey &&
                    (e.QuotaDate == crossing.QuotaDate || e.Phase == GracePhase.Active))) return null;
                var episode = new GraceEpisode(Guid.NewGuid().ToString("N"), crossing.AppKey, crossing.QuotaDate,
                    crossing.ExhaustedAtUtc, crossing.ExhaustedAtUtc + GraceDurationForTesting,
                    GracePhase.Active, null, crossing.EligibleProcesses);
                episodes.Add(episode);
                return episode;
            }
            _accounting.Observe(now, instances, configured, GetLog, observationTimestamp, episodes, Grant);
            for (var i = 0; i < episodes.Count; i++)
            {
                var episode = episodes[i];
                if (episode.Phase != GracePhase.Active) continue;
                // Expiry wins at the deadline, before any external termination is issued.
                if (now >= episode.ExpiresAtUtc)
                    episodes[i] = episode with { Phase = GracePhase.Expired, EndedAtUtc = episode.ExpiresAtUtc };
                else if (episode.Processes.All(p => !instances.Contains(p) && _processes.ConfirmedExited(p)))
                    episodes[i] = episode with { Phase = GracePhase.CompletedByExit, EndedAtUtc = now < episode.StartedAtUtc ? episode.StartedAtUtc : now };
            }
            // Materialize today's zero bucket even when downtime denies the first launch.
            foreach (var rule in configured) _log.GetOrCreate(rule.ProcessName);
            var committed = _db.CommitObservation(logs.Values.Select(log => new DailyLog
            { Date = log.Date, Entries = log.Entries.Where(e => keys.Contains(e.ProcessName)).ToList() }), episodes);
            foreach (var episode in committed.Where(e => !_grace.Any(old => old.Id == e.Id && old.Phase == e.Phase)))
                _logger.TryWrite("Information", "Grace" + episode.Phase);
            Volatile.Write(ref _grace, committed);
            foreach (var name in _sessions.Keys.Where(n => !running.Contains(n)).ToArray()) CloseSession(name);
            foreach (var name in running)
                if (!_sessions.ContainsKey(name)) _sessions[name] = _db.OpenSession(name);

            var decisions = new List<PolicyDecision>();
            var instanceDecisions = new List<(PolicyDecision Decision, ProcessInstance Instance)>();
            var eligible = new List<ProcessInstance>();
            // An inaccessible capture must not escape deadline enforcement just because
            // enumeration could not read it. The terminator revalidates identity and owner.
            var enforcementInstances = instances.Concat(_grace.Where(e => e.Phase == GracePhase.Expired)
                .SelectMany(e => e.Processes).Where(p => keys.Contains(p.AppKey) && !instances.Contains(p) &&
                    !_processes.ConfirmedExited(p))).Distinct().ToArray();
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
                var normal = _rules.Evaluate(snapshot);
                var decision = GracePolicy.Apply(normal, rule.Enabled, _grace, now);
                foreach (var instance in enforcementInstances.Where(p => p.AppKey == rule.ProcessName))
                {
                    var specific = GracePolicy.Apply(normal, rule.Enabled, _grace, now, instance);
                    instanceDecisions.Add((specific, instance));
                    if (rule.Enabled && specific.MayContinue && specific.Grace is null) eligible.Add(instance);
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
            _accounting.SetEligible(eligible);
            var results = new List<TerminationResult>();
            foreach (var (decision, instance) in instanceDecisions.Where(d => d.Decision.TerminationRequired))
                results.Add(await _enforcement.EnforceAsync(decision, instance, ct).ConfigureAwait(false));
            Volatile.Write(ref _enforcementResults, results.AsReadOnly());
            foreach (var result in results.Where(r => r.Outcome is TerminationOutcome.Terminated or TerminationOutcome.AlreadyExited))
                _accounting.Forget(result.Target);
            var next = _downtime.MidnightAfter(now);
            foreach (var episode in _grace.Where(e => e.Phase == GracePhase.Active))
            {
                if (episode.ExpiresAtUtc < next) next = episode.ExpiresAtUtc;
                var finalMinute = episode.ExpiresAtUtc.AddSeconds(-60);
                if (finalMinute > now && finalMinute < next) next = finalMinute;
            }
            foreach (var decision in decisions)
            {
                foreach (var boundary in new[] { decision.DowntimeEnd, decision.NextDowntimeStart })
                    if (boundary > now && boundary < next) next = boundary.Value;
                var rule = configured.FirstOrDefault(r => r.ProcessName == decision.AppKey);
                if (rule is null || !decision.MayContinue || !running.Contains(rule.ProcessName)) continue;
                var limit = rule.GetScheduleForDay(today.DayOfWeek).DailyLimitMinutes * 60L;
                var remaining = limit - _log.GetOrCreate(rule.ProcessName).QuotaSeconds;
                var exhaustion = now + _accounting.Remaining(rule.ProcessName, today, Math.Max(0, remaining));
                if (limit > 0 && remaining > 0 && exhaustion < next) next = exhaustion;
            }
            var delay = next - _time.GetUtcNow();
            _nextWait = delay < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : delay < PollInterval ? delay : PollInterval;
            PublishNotifications(decisions, instanceDecisions, running, today, now);
            // All targets are enforced before any UI subscriber is invoked. WPF enqueues
            // asynchronously; subscriber exceptions are diagnostics, never worker failures.
            foreach (var decision in decisions)
            {
                if (instanceDecisions.Any(d => d.Instance.AppKey == decision.AppKey && d.Decision.TerminationRequired)) Notify(BlockRequested, decision);
                else if (decision.WarnFiveMinutes) Notify(WarnRequested, decision);
            }
        }
        finally { _tickGate.Release(); }
    }

    private void PublishNotifications(IReadOnlyList<PolicyDecision> decisions,
        IReadOnlyList<(PolicyDecision Decision, ProcessInstance Instance)> instances,
        HashSet<string> running, DateOnly today, DateTimeOffset now)
    {
        var facts = new List<NotificationRequest>();
        var revision = Interlocked.Read(ref _configurationRevision);
        foreach (var decision in decisions)
        {
            var rule = _rulesConfig.First(r => r.ProcessName == decision.AppKey);
            var remaining = rule.GetScheduleForDay(today.DayOfWeek).DailyLimitMinutes * 60L -
                (_log.Entries.FirstOrDefault(e => e.ProcessName == decision.AppKey)?.QuotaSeconds ?? 0);
            var candidate = rule.Enabled ? NotificationPolicy.Evaluate(decision, today, remaining,
                running.Contains(decision.AppKey), now) : null;
            if (candidate is not null) facts.Add(candidate with { ConfigurationRevision = revision });
            // A grace milestone takes priority over the optional relaunch-blocked notice.
            if (candidate is null && instances.Any(i => i.Instance.AppKey == decision.AppKey && i.Decision.TerminationRequired) &&
                (!_blockedNotices.TryGetValue(decision.AppKey, out var last) || now - last >= TimeSpan.FromSeconds(30)))
            {
                _blockedNotices[decision.AppKey] = now;
                _notices.Queue(new($"blocked:{decision.AppKey}:{now.UtcTicks}", decision.AppKey,
                    decision.DisplayName, NotificationKind.Blocked, now, now.AddSeconds(15), TimeSpan.Zero,
                    Reason: decision.PrimaryReason, ConfigurationRevision: revision, NextAvailabilityUtc: decision.NextAvailability));
            }
        }
        Volatile.Write(ref _notificationFacts, facts.AsReadOnly());
        // Keep runtime failures deduplicated while the candidate is current. Durable receipts
        // retain date/episode history across restart and configuration changes.
        _noticeAttempts.IntersectWith(facts.Select(n => n.ReceiptKey));
        foreach (var request in facts)
        {
            if (!_noticeAttempts.Add(request.ReceiptKey)) continue;
            _notices.Queue(request);
        }
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
