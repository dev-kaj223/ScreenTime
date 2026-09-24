using System.Text.Json;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class RulesEngineTests
{
    private static readonly DateOnly Wednesday = new(2026, 9, 23);
    private readonly RulesEngine _engine = new();
    private static AppRule Rule(int limit = 60) => new() { ProcessName = "helper", DisplayName = "Helper", DailyLimitMinutes = limit };
    private static DailyLog Log(double used = 0, bool blocked = false) => new()
    {
        Date = Wednesday, Entries = [new() { ProcessName = "helper", UsageMinutes = used, Blocked = blocked }]
    };
    private PolicyDecision Evaluate(double used = 0, bool running = true, bool blocked = false, AppRule? rule = null) =>
        _engine.Evaluate(PolicySnapshot.Capture(rule ?? Rule(), Log(used, blocked), new(16, 0), running));

    [Fact] public void NoAction_WhenUnderLimit() => Assert.Equal(PolicyState.Available, Evaluate(30).State);
    [Fact] public void Block_WhenAtLimit() => Assert.True(Evaluate(60).TerminationRequired);
    [Fact] public void Warn_WhenWithin5MinutesOfLimit()
    {
        var decision = Evaluate(56);
        Assert.True(decision.WarnFiveMinutes);
        Assert.True(decision.MayContinue);
    }
    [Fact] public void NoWarn_WhenWarnAlreadySent()
    {
        var log = Log(56); log.Entries[0].WarningSent = true;
        var decision = _engine.Evaluate(PolicySnapshot.Capture(Rule(), log, new(16, 0), true));
        Assert.False(decision.WarnFiveMinutes);
        Assert.Equal(PolicyState.Warning, decision.State);
    }
    [Fact] public void Block_WhenOutsideTimeWindow()
    {
        var rule = Rule(); rule.AllowedWindowStart = "17:00"; rule.AllowedWindowEnd = "20:00";
        Assert.Equal(PolicyState.TemporaryScheduleRestriction, Evaluate(rule: rule).State);
    }
    [Fact] public void NoAction_WhenInsideTimeWindow()
    {
        var rule = Rule(); rule.AllowedWindowStart = "15:00"; rule.AllowedWindowEnd = "20:00";
        Assert.True(Evaluate(rule: rule).MayLaunch);
    }
    [Fact] public void OverallCap_IsInactive()
    {
        var config = new AppConfig { OverallDailyLimitMinutes = 1, Rules = [Rule()] };
        var log = Log(10); log.TotalUsageMinutes = 1000; log.OverallCapHit = true;
        Assert.True(_engine.Evaluate(PolicySnapshot.Capture(config.Rules[0], log, new(16, 0), true)).MayContinue);
    }
    [Fact] public void StoppedApp_HasStatusWithoutTermination()
    {
        var decision = Evaluate(60, running: false);
        Assert.False(decision.MayLaunch);
        Assert.False(decision.TerminationRequired);
    }
    [Fact] public void Relaunched_OverQuotaIsDenied() => Assert.True(Evaluate(60, blocked: true).TerminationRequired);
    [Fact] public void Relaunched_NoTerminationIfNotRunning() => Assert.False(Evaluate(60, running: false, blocked: true).TerminationRequired);
    [Fact] public void LegacyBlockedFlag_CannotDenyRemainingQuota() => Assert.True(Evaluate(10, blocked: true).MayContinue);
    [Fact] public void BreakIntervalReached_IsInactive()
    {
        var rule = Rule(120); rule.BreakEveryMinutes = 30; rule.BreakDurationMinutes = 5;
        Assert.True(Evaluate(30, rule: rule).MayContinue);
    }
    [Fact] public void BreakIntervalNotReached_IsInactive()
    {
        var rule = Rule(120); rule.BreakEveryMinutes = 30; rule.BreakDurationMinutes = 5;
        Assert.True(Evaluate(20, rule: rule).MayContinue);
    }
    [Fact] public void Block_WhenWeekdaySpecificLimitReached()
    {
        var rule = Rule(0);
        rule.DaySchedules = [new() { DayOfWeek = DayOfWeek.Wednesday, DailyLimitMinutes = 30 }];
        Assert.Equal(PolicyState.DailyQuotaBlocked, Evaluate(30, rule: rule).State);
    }
    [Fact] public void Block_WhenOutsideWeekdaySpecificWindow()
    {
        var rule = Rule();
        rule.DaySchedules = [new() { DayOfWeek = DayOfWeek.Wednesday, AllowedWindowStart = "12:00", AllowedWindowEnd = "14:00" }];
        Assert.Equal(PolicyState.TemporaryScheduleRestriction, Evaluate(rule: rule).State);
    }
    [Fact] public void NoAction_WhenAnotherDayHasShorterLimit()
    {
        var rule = Rule(60);
        rule.DaySchedules = [new() { DayOfWeek = DayOfWeek.Monday, DailyLimitMinutes = 30 }];
        Assert.True(Evaluate(45, rule: rule).MayContinue);
    }
    [Fact] public void EvaluationAndCapture_DoNotMutateAnyInput_OrCreateMissingUsage()
    {
        var config = new AppConfig { Rules = [Rule()] };
        var log = new DailyLog { Date = Wednesday };
        var before = JsonSerializer.Serialize(new { config, log });
        var snapshot = PolicySnapshot.Capture(config.Rules[0], log, new(16, 0), true);
        var decision = _engine.Evaluate(snapshot);
        Assert.Equal(decision, _engine.Evaluate(snapshot));
        Assert.Equal(before, JsonSerializer.Serialize(new { config, log }));
        config.Rules[0].DailyLimitMinutes = 1;
        log.GetOrCreate("helper").UsageMinutes = 100;
        Assert.Equal(decision, _engine.Evaluate(snapshot)); // Snapshot owns scalar copies.
    }
    [Fact] public void DisabledRule_ImposesNoRestrictionOrWarning()
    {
        var rule = Rule(1); rule.Enabled = false;
        var decision = Evaluate(100, rule: rule);
        Assert.True(decision.MayLaunch); Assert.True(decision.MayContinue);
        Assert.False(decision.WarnFiveMinutes); Assert.False(decision.TerminationRequired);
    }
    [Theory]
    [InlineData(15, 0)] [InlineData(20, 0)]
    public void LegacyWindowEndpoints_RemainInclusive(int hour, int minute)
    {
        var rule = Rule(); rule.AllowedWindowStart = "15:00"; rule.AllowedWindowEnd = "20:00";
        Assert.True(_engine.Evaluate(PolicySnapshot.Capture(rule, Log(), new(hour, minute), true)).MayContinue);
    }
    [Fact] public void UnlimitedDay_RemainsUnlimited() => Assert.True(Evaluate(1000, rule: Rule(0)).MayContinue);
    [Fact] public void ScheduleAndQuotaReasons_AreBothRetained()
    {
        var rule = Rule(); rule.AllowedWindowStart = "17:00"; rule.AllowedWindowEnd = "20:00";
        var outside = PolicySnapshot.Capture(rule, Log(60), new(16, 0), true);
        var decision = _engine.Evaluate(outside);
        Assert.Equal(PolicyReason.OutsideAllowedWindow | PolicyReason.DailyQuotaExhausted, decision.Reasons);
        var inside = _engine.Evaluate(outside with { LocalTime = new(18, 0) });
        Assert.Equal(PolicyState.DailyQuotaBlocked, inside.State);
        Assert.False(inside.MayLaunch);
    }
}
