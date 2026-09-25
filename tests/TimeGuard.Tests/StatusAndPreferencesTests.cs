using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class StatusAndPreferencesTests
{
    [Fact]
    public async Task Status_IsCommittedEnabledCollection_WithIndependentFacts_AndImmutableEarlierPublication()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock();
        var date = DateOnly.FromDateTime(clock.Now.Date);
        db.UpsertUsageEntry(date, new() { ProcessName = "first", QuotaSeconds = 58, ObservedSeconds = 58 });
        var first = new AppRule { ProcessName = "first", DisplayName = "First", DailyLimitMinutes = 1 };
        var second = new AppRule { ProcessName = "second", DisplayName = "Second", DailyLimitMinutes = 30,
            BlockedPeriods = [new() { StartDayOfWeek = date.DayOfWeek, StartMinute = 0, EndMinute = 1020 }] };
        var disabled = new AppRule { ProcessName = "disabled", Enabled = false };
        var instance = new ProcessInstance("first", 11, 100, 1);
        await using var monitor = new MonitorService(db, new(), new() { Rules = [first, second, disabled] },
            processes: new FakeProcesses(() => [instance]), terminator: new FakeTerminator(), time: clock);
        Assert.Null(monitor.Status);
        await monitor.TickAsync();
        var original = monitor.Status!;
        Assert.Equal(2, original.Apps.Count);
        Assert.Equal(58, original.Apps[0].Facts.QuotaSeconds);
        Assert.True(original.Apps[0].Facts.IsRunning);
        var down = original.Apps[1];
        Assert.Equal(0, down.Facts.QuotaSeconds);
        Assert.Equal(30, down.Facts.DailyLimitMinutes);
        Assert.Equal(PolicyState.TemporaryDowntime, down.Decision.State);
        Assert.False(down.Decision.MayLaunch);
        Assert.Equal(clock.Now.Date.AddHours(17), down.Decision.NextAvailability!.Value.UtcDateTime);
        Assert.NotNull(down.Decision.NextDowntimeStart);
        clock.Now = clock.Now.AddSeconds(5);
        await monitor.TickAsync();
        var grace = monitor.Status!.Apps[0];
        Assert.Equal(60, grace.Facts.QuotaSeconds);
        Assert.Equal(63, grace.ObservedSeconds);
        Assert.Equal(3, grace.GraceSeconds);
        Assert.Equal(db.LoadGraceEpisodes().Single().ExpiresAtUtc, grace.Decision.Grace!.ExpiresAtUtc);
        Assert.Equal(58, original.Apps[0].Facts.QuotaSeconds);
        Assert.Null(original.Apps[0].Decision.Grace);
        Assert.Equal(0, monitor.Status.Apps[1].Facts.QuotaSeconds);
    }

    [Fact]
    public void Preferences_DefaultsPresetsCustomRoundtrip_ColorsReset_AndIndependentMilestones()
    {
        var standard = NotificationPreferences.Parse(null);
        Assert.All(Enum.GetValues<NotificationKind>(), kind => Assert.True(standard.Allows(kind)));
        Assert.Equal(60, standard.CountdownSeconds);
        Assert.Null(standard.InformationalColor); Assert.Null(standard.WarningColor); Assert.Null(standard.CriticalColor);
        var minimal = NotificationPreferences.Minimal;
        Assert.False(minimal.QuotaTenMinutes); Assert.False(minimal.QuotaFiveMinutes); Assert.False(minimal.GraceFiveMinutes);
        Assert.True(minimal.GraceStarted); Assert.True(minimal.GraceFinalMinute); Assert.True(minimal.Blocked);
        var custom = standard with { Preset = NotificationPreset.Custom, QuotaTenMinutes = false, CountdownSeconds = 15,
            InformationalColor = "#123456", WarningColor = "#abcdef", CriticalColor = "#FF0000" };
        Assert.Equal(custom, NotificationPreferences.Parse(custom.Serialize()));
        Assert.False(custom.Allows(NotificationKind.QuotaTenMinutes));
        Assert.True(custom.Allows(NotificationKind.QuotaFiveMinutes));
        Assert.Equal("#abcdef", custom.ColorFor(NotificationKind.GraceStarted));
        Assert.Equal("#FF0000", custom.ColorFor(NotificationKind.Blocked));
        Assert.Equal(standard, NotificationPreferences.Standard);
    }

    [Theory] [InlineData(0)] [InlineData(61)] [InlineData(-1)]
    public void InvalidCountdownRejected(int seconds) => Assert.Throws<ArgumentException>(() =>
        (NotificationPreferences.Standard with { CountdownSeconds = seconds }).Serialize());

    [Theory] [InlineData("red")] [InlineData("#12345")] [InlineData("#00112233")]
    public void InvalidColorRejected(string color) => Assert.Throws<ArgumentException>(() =>
        (NotificationPreferences.Standard with { CriticalColor = color }).Serialize());

    [Fact]
    public void CountdownPreferences_DelayDisplayOnly_OriginalDeadlineNeverChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new NotificationRequest("a", "a", "A", NotificationKind.GraceFinalMinute,
            now, now.AddSeconds(60), TimeSpan.FromSeconds(60), "episode", now.AddSeconds(60));
        var options = NotificationPreferences.Standard with { CountdownSeconds = 15 };
        Assert.False(options.Ready(request, now));
        Assert.False(options.Ready(request, now.AddSeconds(44)));
        Assert.True(options.Ready(request, now.AddSeconds(45)));
        Assert.False(options.Ready(request, now.AddSeconds(60)));
        Assert.False((options with { GraceFinalMinute = false }).Ready(request, now.AddSeconds(50)));
        Assert.Equal(now.AddSeconds(60), request.GraceDeadlineUtc);
    }

    [Fact]
    public async Task SuppressedPreferences_DoNotRebaseAccounting_GrantRenewalOrExpiry()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        var clock = new TestClock(); var date = DateOnly.FromDateTime(clock.Now.Date);
        db.UpsertUsageEntry(date, new() { ProcessName = "helper", QuotaSeconds = 58, ObservedSeconds = 58 });
        var target = new ProcessInstance("helper", 11, 100, 1);
        var terminator = new FakeTerminator();
        await using var monitor = new MonitorService(db, new(), new() { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 1 }] },
            processes: new FakeProcesses(() => [target]), terminator: terminator, time: clock);
        await monitor.TickAsync();
        var store = new NotificationPreferenceStore(profile.Runtime.Paths);
        store.Save(NotificationPreferences.Standard with { QuotaTenMinutes = false, QuotaFiveMinutes = false,
            GraceStarted = false, GraceFiveMinutes = false, GraceFinalMinute = false, Blocked = false });
        clock.Now = clock.Now.AddSeconds(3); await monitor.TickAsync();
        var deadline = Assert.Single(db.LoadGraceEpisodes()).ExpiresAtUtc;
        Assert.Equal(60, monitor.Status!.Apps[0].Facts.QuotaSeconds);
        Assert.Equal(1, monitor.Status.Apps[0].GraceSeconds);
        using var blockedPreferences = new FileStream(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failure = Record.Exception(() => store.Save(NotificationPreferences.Standard));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        clock.Now = deadline; await monitor.TickAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(target, Assert.Single(terminator.Targets));
        Assert.Equal(GracePhase.Expired, Assert.Single(db.LoadGraceEpisodes()).Phase);
        Assert.Equal(deadline, Assert.Single(db.LoadGraceEpisodes()).ExpiresAtUtc);
    }

    [Fact]
    public void PreferenceFile_AtomicRoundtrip_CorruptionRetained_AndNoPolicyDatabase()
    {
        using var profile = new TempProfile();
        var store = new NotificationPreferenceStore(profile.Runtime.Paths);
        Assert.Equal(NotificationPreferences.Standard, store.Load());
        store.Save(NotificationPreferences.Minimal);
        Assert.Equal(NotificationPreferences.Minimal, store.Load());
        File.WriteAllText(store.Path, "corrupt");
        Assert.Equal(NotificationPreferences.Standard, store.Load());
        Assert.Equal("corrupt", File.ReadAllText(store.Path));
        Assert.False(File.Exists(profile.Runtime.Paths.DatabasePath));
    }
}
