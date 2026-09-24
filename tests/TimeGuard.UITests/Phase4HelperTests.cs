using System.Diagnostics;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

public class Phase4HelperTests
{
    private sealed class CalendarClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 21, 23, 54, 58, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OwnedHelper_MidnightDowntime_CapturedContinues_NewDenied_ExitOrExpiry(bool exit)
    {
        using var fixture = new SeededAppFixture();
        var original = fixture.LaunchHelper(headless: true);
        var db = fixture.OpenDatabase();
        var clock = new CalendarClock();
        var rule = new AppRule { ProcessName = "screentime.testprocess", DailyLimitMinutes = 1,
            BlockedPeriods = [new() { StartDayOfWeek = DayOfWeek.Tuesday, StartMinute = 0, EndMinute = 480 }] };
        db.SaveRule(rule);
        db.UpsertUsageEntry(new(2026, 9, 21), new() { ProcessName = rule.ProcessName, ObservedSeconds = 58, QuotaSeconds = 58 });
        var scope = new TestProcessScope(fixture.Runtime.Paths);
        await using var monitor = new MonitorService(db, new(), new() { Rules = [rule] },
            processes: new WindowsProcessMonitor(isAllowedTarget: scope.Contains),
            terminator: new WindowsProcessTerminator(scope.Contains), time: clock);
        await monitor.TickAsync(); clock.Now = clock.Now.AddSeconds(5); await monitor.TickAsync();
        var episode = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 0, 15, 0, TimeSpan.Zero), episode.ExpiresAtUtc);
        clock.Now = new(2026, 9, 21, 23, 59, 58, TimeSpan.Zero); await monitor.TickAsync();
        clock.Now = clock.Now.AddSeconds(5); await monitor.TickAsync();
        Assert.True(Alive(original)); Assert.False(monitor.Decisions.Single().MayLaunch);
        var tuesday = db.LoadLog(new(2026, 9, 22)).Entries.Single();
        Assert.Equal(0, tuesday.QuotaSeconds); Assert.Equal(3, tuesday.GraceSeconds); Assert.Equal(3, tuesday.ObservedSeconds);
        var second = fixture.LaunchHelper(headless: true); await monitor.TickAsync();
        Assert.False(Alive(second)); Assert.True(Alive(original));
        if (exit)
        {
            clock.Now = new(2026, 9, 22, 0, 5, 0, TimeSpan.Zero);
            original.Terminate(); await Until(() => !Alive(original), TimeSpan.FromSeconds(3));
        }
        else clock.Now = episode.ExpiresAtUtc;
        await monitor.TickAsync(); Assert.False(Alive(original));
        Assert.Equal(exit ? GracePhase.CompletedByExit : GracePhase.Expired, db.LoadGraceEpisodes().Single().Phase);
        var replacement = fixture.LaunchHelper(headless: true); await monitor.TickAsync(); Assert.False(Alive(replacement));
        Assert.Equal(0, db.LoadLog(new(2026, 9, 22)).Entries.Single().QuotaSeconds);
    }

    [Fact]
    public async Task OwnedHelper_Grant_RelaunchDenied_RestartPreservesDeadline_Expires()
    {
        using var fixture = new SeededAppFixture();
        using var other = new SeededAppFixture();
        var untouched = other.LaunchHelper(headless: true);
        var original = fixture.LaunchHelper(headless: true);
        var db = fixture.OpenDatabase();
        var rule = new AppRule { ProcessName = "screentime.testprocess", DailyLimitMinutes = 1 };
        db.SaveRule(rule);
        var date = DateOnly.FromDateTime(DateTime.Today);
        db.UpsertUsageEntry(date, new() { ProcessName = rule.ProcessName, QuotaSeconds = 58, ObservedSeconds = 58 });
        var scope = new TestProcessScope(fixture.Runtime.Paths);
        var fullDuration = Environment.GetEnvironmentVariable("SCREENTIME_PHASE4_FULL_DURATION") == "1";
        var duration = fullDuration ? GraceEpisode.DefaultDuration : TimeSpan.FromSeconds(12);
        MonitorService Create() => new(db, new(), new() { Rules = [rule] },
            processes: new WindowsProcessMonitor(isAllowedTarget: scope.Contains),
            terminator: new WindowsProcessTerminator(scope.Contains)) { GraceDurationForTesting = duration };
        await using var first = Create();
        first.Start();
        await Until(() => db.LoadGraceEpisodes().Count == 1, TimeSpan.FromSeconds(10));
        var episode = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(duration, episode.ExpiresAtUtc - episode.StartedAtUtc);
        Assert.True(Alive(original));
        var second = fixture.LaunchHelper(headless: true);
        await Until(() => !Alive(second), TimeSpan.FromSeconds(7));
        Assert.True(Alive(original));
        await first.StopAsync();
        await using var restarted = Create();
        restarted.Start();
        await Until(() => restarted.Decisions.Count == 1, TimeSpan.FromSeconds(3));
        Assert.Equal(episode.ExpiresAtUtc, Assert.Single(db.LoadGraceEpisodes()).ExpiresAtUtc);
        Assert.True(Alive(original));
        var samples = 0;
        while (DateTimeOffset.UtcNow < episode.ExpiresAtUtc.AddMilliseconds(-250))
        {
            Assert.True(Alive(original));
            Assert.True(Alive(untouched));
            Assert.Equal(GracePhase.Active, Assert.Single(db.LoadGraceEpisodes()).Phase);
            samples++;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000,
                Math.Max(1, (episode.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalMilliseconds - 250))));
        }
        await Until(() => !Alive(original), TimeSpan.FromSeconds(3));
        var ended = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(GracePhase.Expired, ended.Phase);
        Assert.Equal(episode.ExpiresAtUtc, ended.EndedAtUtc);
        Assert.Equal(episode.ExpiresAtUtc, ended.ExpiresAtUtc);
        Assert.True(Alive(untouched));
        var replacement = fixture.LaunchHelper(headless: true);
        await Until(() => !Alive(replacement), TimeSpan.FromSeconds(7));
        await restarted.StopAsync();
        if (fullDuration)
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "phase4-full-duration.txt"),
                $"Started={episode.StartedAtUtc:O}\nDeadline={episode.ExpiresAtUtc:O}\nScenarioCompleted={DateTimeOffset.UtcNow:O}\nDuration={duration}\nAliveSamples={samples}\nPhase={ended.Phase}\nRestart preserved deadline; second and later helpers denied; other fixture survived.\n");
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < timeout, "Timed out waiting for real helper transition.");
            await Task.Delay(50);
        }
    }
    private static bool Alive(OwnedProcessIdentity identity)
    {
        try { using var p = Process.GetProcessById(identity.Id); return identity.Matches(p); }
        catch (ArgumentException) { return false; }
    }
}
