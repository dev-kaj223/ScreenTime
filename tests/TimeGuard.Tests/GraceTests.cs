using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;
using static TimeGuard.Tests.DowntimeEvaluatorTests;

namespace TimeGuard.Tests;

public class GraceTests
{
    private static readonly ProcessInstance Original = new("helper", 11, 100, 1);
    private static readonly ProcessInstance Second = new("helper", 12, 200, 1);
    private sealed class Harness : IAsyncDisposable
    {
        public TempProfile Profile { get; } = new();
        public DatabaseService Db { get; }
        public TestClock Clock { get; } = new() { Now = At("2026-09-21T12:00:00") };
        public AppRule Rule { get; } = new() { ProcessName = "helper", DailyLimitMinutes = 1 };
        public ProcessInstance[] Processes { get; set; } = [Original];
        public FakeTerminator Kill { get; } = new() { Outcome = TerminationOutcome.AccessDenied };
        public MonitorService Monitor { get; private set; } = null!;
        public DateOnly Date => DateOnly.FromDateTime(Clock.Now.UtcDateTime);
        public Harness(long used = 58)
        {
            Db = new(Profile.Runtime.Paths);
            Db.UpsertUsageEntry(Date, new() { ProcessName = "helper", QuotaSeconds = used, ObservedSeconds = used });
            Create();
        }
        private void Create() => Monitor = new(Db, new(), new() { Rules = [Rule] },
            processes: new FakeProcesses(() => Processes), terminator: Kill, time: Clock);
        public async Task Restart() { await Monitor.StopAsync(); Create(); }
        public async Task Tick(double seconds = 0) { Clock.Now = Clock.Now.AddSeconds(seconds); await Monitor.TickAsync(); }
        public GraceEpisode Episode => Assert.Single(Db.LoadGraceEpisodes());
        public UsageEntry Usage => Db.LoadLog(Date).GetOrCreate("helper");
        public async ValueTask DisposeAsync() { await Monitor.StopAsync(); Profile.Dispose(); }
    }

    [Fact] public async Task ExactCrossing_AtomicGrant_ContinuousCapturedSet_AndRemainder()
    {
        await using var h = new Harness();
        h.Processes = [Original, Second];
        await h.Tick(); await h.Tick(5);
        Assert.Equal(At("2026-09-21T12:00:02"), h.Episode.StartedAtUtc);
        Assert.Equal(At("2026-09-21T12:20:02"), h.Episode.ExpiresAtUtc);
        Assert.Equal(2, h.Episode.Processes.Count);
        Assert.Equal(60, h.Usage.QuotaSeconds); Assert.Equal(63, h.Usage.ObservedSeconds); Assert.Equal(3, h.Usage.GraceSeconds);
        Assert.Empty(h.Kill.Targets);
        Assert.False(h.Monitor.Decisions.Single().MayLaunch);
        Assert.Equal(PolicyState.QuotaExhaustedGrace, h.Monitor.Decisions.Single().State);
        await h.Tick(5); Assert.Equal(8, h.Usage.GraceSeconds);
        Assert.Equal(60, h.Usage.QuotaSeconds);
    }

    [Fact] public async Task FractionalQuotaCarry_ProducesExactCrossing()
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(.75); await h.Tick(4.25);
        Assert.Equal(At("2026-09-21T12:00:02"), h.Episode.StartedAtUtc);
        Assert.Equal(3, h.Usage.GraceSeconds); Assert.Equal(63, h.Usage.ObservedSeconds);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AlreadyExhausted_StartupAndRestart_NeverGrant(bool restart)
    {
        await using var h = new Harness(60); await h.Tick(); if (restart) await h.Restart(); await h.Tick(5);
        Assert.Empty(h.Db.LoadGraceEpisodes()); Assert.False(h.Monitor.Decisions.Single().MayLaunch);
        Assert.Equal(2, h.Kill.Targets.Count);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NewInstanceAtCrossing_AndPidReuse_DoNotJoin(bool reuse)
    {
        await using var h = new Harness(); await h.Tick();
        var added = reuse ? new ProcessInstance("helper", Original.ProcessId, 999, 1) : Second;
        h.Processes = [Original, added, new("unrelated", 77, 88, 1)];
        await h.Tick(5);
        Assert.Equal(Original, Assert.Single(h.Episode.Processes));
        Assert.Equal(added, Assert.Single(h.Kill.Targets));
        await h.Tick(5); Assert.DoesNotContain(Original, h.Kill.Targets);
    }

    [Fact] public async Task ReplacementWithoutContinuity_NeverGrants()
    {
        await using var h = new Harness(); await h.Tick(); h.Processes = [Second]; await h.Tick(5);
        Assert.Empty(h.Db.LoadGraceEpisodes()); Assert.Equal(58, h.Usage.QuotaSeconds);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ConfirmedExitOrCrash_Completes_ReplacementDenied(bool replacementPresent)
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5);
        h.Processes = replacementPresent ? [Second] : []; await h.Tick(1);
        Assert.Equal(GracePhase.CompletedByExit, h.Episode.Phase);
        Assert.Equal(h.Clock.Now, h.Episode.EndedAtUtc);
        h.Processes = [Second]; await h.Restart(); await h.Tick();
        Assert.Contains(Second, h.Kill.Targets); Assert.Equal(GracePhase.CompletedByExit, h.Episode.Phase);
    }

    [Fact] public async Task PartialExit_KeepsRemainingCapturedInstanceOnly()
    {
        await using var h = new Harness(); h.Processes = [Original, Second]; await h.Tick(); await h.Tick(5);
        h.Processes = [Second]; await h.Tick(5);
        Assert.Equal(GracePhase.Active, h.Episode.Phase); Assert.Empty(h.Kill.Targets);
        h.Processes = []; await h.Tick(); Assert.Equal(GracePhase.CompletedByExit, h.Episode.Phase);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Restart_PreservesDeadline_ValidatesOriginal_AndExpires(int scenario)
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5); var original = h.Episode;
        h.Clock.Now = original.StartedAtUtc.AddMinutes(10);
        if (scenario == 1) h.Processes = [Second];
        if (scenario == 2) h.Clock.Now = original.ExpiresAtUtc.AddSeconds(1);
        await h.Restart(); await h.Tick();
        Assert.Equal(original.ExpiresAtUtc, h.Episode.ExpiresAtUtc);
        Assert.Equal(scenario == 0 ? GracePhase.Active : scenario == 1 ? GracePhase.CompletedByExit : GracePhase.Expired, h.Episode.Phase);
        if (scenario == 0) Assert.Empty(h.Kill.Targets); else Assert.Single(h.Kill.Targets);
        Assert.Equal(3, h.Usage.GraceSeconds); // Never reconstruct an outage.
    }

    [Fact] public async Task ExactExpiry_FailedKill_DoesNotExtend_AndRetries()
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5); var deadline = h.Episode.ExpiresAtUtc;
        h.Clock.Now = deadline.AddTicks(-1); await h.Tick(); Assert.Empty(h.Kill.Targets);
        h.Clock.Now = deadline; await h.Tick(); Assert.Equal(GracePhase.Expired, h.Episode.Phase);
        Assert.Single(h.Kill.Targets); Assert.Equal(TerminationOutcome.AccessDenied, h.Monitor.EnforcementResults.Single().Outcome);
        await h.Tick(5); Assert.Equal(2, h.Kill.Targets.Count); Assert.Equal(deadline, h.Episode.ExpiresAtUtc);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Sleep_DoesNotChargeOrExtend_ResumeEnforces(bool beyond)
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5); var deadline = h.Episode.ExpiresAtUtc;
        h.Monitor.NotifySuspend(); h.Clock.Now = beyond ? deadline.AddMinutes(5) : h.Clock.Now.AddSeconds(10);
        h.Monitor.NotifyResume(); await h.Tick();
        Assert.Equal(3, h.Usage.GraceSeconds); Assert.Equal(60, h.Usage.QuotaSeconds);
        Assert.Equal(deadline, h.Episode.ExpiresAtUtc);
        Assert.Equal(beyond ? GracePhase.Expired : GracePhase.Active, h.Episode.Phase);
        Assert.Equal(beyond ? 1 : 0, h.Kill.Targets.Count);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Midnight_OriginalContinues_FreshQuotaUntouched_ExitOrExpiry_ThenAvailability(bool adjacent, bool exit)
    {
        await using var h = new Harness(); h.Clock.Now = At("2026-09-21T23:54:58");
        h.Rule.BlockedPeriods = [Period(DayOfWeek.Tuesday, 0, 480)];
        if (adjacent) h.Rule.BlockedPeriods.Add(Period(DayOfWeek.Tuesday, 480, 1020));
        h.Monitor.ReloadConfig(new() { Rules = [h.Rule] });
        await h.Tick(); await h.Tick(5);
        Assert.Equal(At("2026-09-22T00:15:00"), h.Episode.ExpiresAtUtc);
        h.Clock.Now = At("2026-09-21T23:59:58"); await h.Tick(); await h.Tick(5);
        Assert.Equal(0, h.Usage.QuotaSeconds); Assert.Equal(3, h.Usage.GraceSeconds); Assert.Equal(3, h.Usage.ObservedSeconds);
        Assert.False(h.Monitor.Decisions.Single().MayLaunch); Assert.Empty(h.Kill.Targets);
        h.Processes = [Original, Second]; await h.Tick(); Assert.Equal(Second, Assert.Single(h.Kill.Targets));
        h.Kill.Targets.Clear(); h.Processes = exit ? [] : [Original];
        h.Clock.Now = exit ? At("2026-09-22T00:05:00") : h.Episode.ExpiresAtUtc; await h.Tick();
        Assert.Equal(exit ? GracePhase.CompletedByExit : GracePhase.Expired, h.Episode.Phase);
        Assert.Equal(exit ? 0 : 1, h.Kill.Targets.Count);
        Assert.False(h.Monitor.Decisions.Single().MayLaunch);
        h.Processes = []; h.Clock.Now = At("2026-09-22T08:00:00"); await h.Tick();
        Assert.Equal(!adjacent, h.Monitor.Decisions.Single().MayLaunch);
        h.Clock.Now = At("2026-09-22T17:00:00"); await h.Tick();
        Assert.True(h.Monitor.Decisions.Single().MayLaunch); Assert.Equal(0, h.Usage.QuotaSeconds);
    }

    [Fact] public async Task ExpiredCarriedSurvivor_CannotConsumeNextDayQuota_EvenWithoutDowntime()
    {
        await using var h = new Harness(); h.Clock.Now = At("2026-09-21T23:54:58"); await h.Tick(); await h.Tick(5);
        h.Clock.Now = h.Episode.ExpiresAtUtc; await h.Tick(); await h.Tick(5);
        Assert.Equal(0, h.Usage.QuotaSeconds); Assert.Equal(0, h.Usage.GraceSeconds);
        Assert.Equal(2, h.Kill.Targets.Count); Assert.True(h.Monitor.Decisions.Single().MayLaunch);
        h.Processes = [Original, Second]; await h.Tick(); await h.Tick(5);
        Assert.Equal(5, h.Usage.QuotaSeconds); // The new permitted session still pays quota if the old kill fails.
        Assert.DoesNotContain(Second, h.Kill.Targets);
    }

    [Fact] public async Task AllowanceDecrease_NoRetroactiveGrant_AndRaiseLowerCannotMintSecondEpisode()
    {
        await using var h = new Harness(61); h.Rule.DailyLimitMinutes = 2; h.Monitor.ReloadConfig(new() { Rules = [h.Rule] });
        await h.Tick(); h.Rule.DailyLimitMinutes = 1; h.Monitor.ReloadConfig(new() { Rules = [h.Rule] }); await h.Tick();
        Assert.Empty(h.Db.LoadGraceEpisodes()); Assert.Single(h.Kill.Targets);
        await using var g = new Harness(); await g.Tick(); await g.Tick(5); var id = g.Episode.Id;
        g.Processes = []; await g.Tick(); g.Rule.DailyLimitMinutes = 2; g.Monitor.ReloadConfig(new() { Rules = [g.Rule] });
        g.Processes = [Second]; await g.Tick(); await g.Tick(30); await g.Tick(30);
        Assert.Equal(id, g.Episode.Id); Assert.Equal(GracePhase.CompletedByExit, g.Episode.Phase); Assert.Contains(Second, g.Kill.Targets);
        g.Rule.DailyLimitMinutes = 1; g.Monitor.ReloadConfig(new() { Rules = [g.Rule] }); await g.Tick(); await g.Restart(); await g.Tick();
        Assert.Equal(id, g.Episode.Id);
    }

    [Fact] public async Task DowntimeAlone_AndSimultaneousExhaustion_DoNotGrant()
    {
        await using var h = new Harness(); h.Clock.Now = At("2026-09-21T11:59:58");
        h.Rule.BlockedPeriods = [Period(DayOfWeek.Monday, 720, 780)]; h.Monitor.ReloadConfig(new() { Rules = [h.Rule] });
        await h.Tick(); await h.Tick(5); Assert.Empty(h.Db.LoadGraceEpisodes()); Assert.Single(h.Kill.Targets);
        Assert.Equal(60, h.Usage.QuotaSeconds); Assert.Equal(0, h.Usage.GraceSeconds);
    }

    private sealed class InaccessibleProcesses : IProcessMonitor
    {
        public IReadOnlyList<ProcessInstance> Snapshot(IReadOnlyCollection<string> keys) => [];
        public bool ConfirmedExited(ProcessInstance instance) => false;
    }

    [Fact] public async Task InaccessibleCapture_IsNotFalselyCompleted_AndExpiryStillAttemptsRevalidation()
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5); await h.Monitor.StopAsync();
        var kill = new FakeTerminator { Outcome = TerminationOutcome.AccessDenied };
        await using var monitor = new MonitorService(h.Db, new(), new() { Rules = [h.Rule] },
            processes: new InaccessibleProcesses(), terminator: kill, time: h.Clock);
        await monitor.TickAsync(); Assert.Equal(GracePhase.Active, h.Episode.Phase);
        h.Clock.Now = h.Episode.ExpiresAtUtc; await monitor.TickAsync();
        Assert.Equal(GracePhase.Expired, h.Episode.Phase); Assert.Equal(Original, Assert.Single(kill.Targets));
    }

    [Fact] public async Task CaptureDoesNotBecomeAnAppWidePermission_AndDisabledRuleStillOverrides()
    {
        await using var h = new Harness(); await h.Tick(); await h.Tick(5);
        var normal = new RulesEngine().Evaluate(PolicySnapshot.Capture(h.Rule, h.Db.LoadLog(h.Date), new TimeOnly(12, 0), true));
        Assert.False(GracePolicy.Apply(normal, true, [h.Episode], h.Clock.Now).MayContinue);
        Assert.True(GracePolicy.Apply(normal, true, [h.Episode], h.Clock.Now, Original).MayContinue);
        Assert.False(GracePolicy.Apply(normal, true, [h.Episode], h.Clock.Now, Second).MayContinue);
        Assert.False(GracePolicy.Apply(normal with { MayContinue = true, MayLaunch = true }, true,
            [h.Episode], h.Episode.ExpiresAtUtc, Original).MayContinue);
        h.Rule.Enabled = false;
        var disabled = new RulesEngine().Evaluate(PolicySnapshot.Capture(h.Rule, h.Db.LoadLog(h.Date), new TimeOnly(12, 0), true));
        Assert.True(GracePolicy.Apply(disabled, false, [h.Episode], h.Clock.Now, Second).MayContinue);
    }
}
