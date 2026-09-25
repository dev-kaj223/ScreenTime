using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.UITests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace TimeGuard.UITests;

public sealed class ReleaseFactAttribute : FactAttribute
{
    public ReleaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCREENTIME_RELEASE_DIRECTORY")))
            Skip = "Run separately against an extracted verified release package using SCREENTIME_RELEASE_DIRECTORY.";
    }
}

public class ReleaseSmokeTests(ITestOutputHelper output)
{
    private static string Executable => Path.Combine(Environment.GetEnvironmentVariable("SCREENTIME_RELEASE_DIRECTORY")
        ?? throw new InvalidOperationException("Release package path required."), "ScreenTime.exe");
    private class ReleaseFixture : SeededAppFixture { protected override string AppExecutable => Executable; }
    private sealed class FirstRunFixture : AppFixture { protected override string AppExecutable => Executable; }
    private sealed class GrantFixture : ReleaseFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            var db = OpenDatabase();
            db.SaveRule(new() { ProcessName = "screentime.testprocess", DailyLimitMinutes = 1 });
            db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Now), new() { ProcessName = "screentime.testprocess", QuotaSeconds = 58, ObservedSeconds = 58 });
        }
    }
    private static void Signal(string name) { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); }
    private static void Password(FlaUI.Core.AutomationElements.Window prompt, string password)
    {
        prompt.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)).Click();
        Keyboard.Type(password); prompt.FindButton("Unlock").Invoke();
    }
    private static bool Alive(OwnedProcessIdentity identity)
    {
        try { using var process = Process.GetProcessById(identity.Id); return identity.Matches(process); }
        catch (ArgumentException) { return false; }
    }
    private static async Task Until(Func<bool> condition, int seconds = 12)
    {
        var time = Stopwatch.StartNew();
        while (!condition()) { Assert.True(time.Elapsed < TimeSpan.FromSeconds(seconds), "Packaged application transition timed out."); await Task.Delay(100); }
    }

    [ReleaseFact]
    public void ExtractedPackage_FirstRunCreatesOnlyDisposableProfile_AndTrayStarts()
    {
        using var fixture = new FirstRunFixture();
        var setup = fixture.App.WaitForWindow(fixture.Automation, "Welcome to ScreenTime");
        foreach (var id in new[] { "PasswordBox", "ConfirmBox" })
        {
            setup.FindFirstDescendant(cf => cf.ByAutomationId(id)).Click(); Keyboard.Type(AppFixture.TestPassword);
        }
        setup.FindButton("Get Started →").Invoke();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { using var status = EventWaitHandle.OpenExisting(fixture.Runtime.StatusEventName); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }, TimeSpan.FromSeconds(5)));
        Assert.False(fixture.OpenDatabase().LoadConfig().IsFirstRun);
        BrandingTests.CaptureShellIcon(fixture, taskbar: false, prefix: "phase9");
        var settings = fixture.App.WaitForWindow(fixture.Automation, "ScreenTime — Main");
        Assert.DoesNotContain(fixture.App.GetAllTopLevelWindows(fixture.Automation), w => w.Title == "Protected Access");
        settings.FindButton("➕ Add Rule").Invoke();
        var editor = fixture.App.WaitForWindow(fixture.Automation, "Edit App Rule");
        editor.FindTextBox("DisplayNameBox").AsTextBox().Text = "Package UX fixture";
        editor.FindTextBox("ProcessNameBox").AsTextBox().Text = "package-ux-fixture";
        editor.FindTextBox("MondayHoursBox").AsTextBox().Text = "1";
        editor.FindTextBox("MondayMinutesBox").AsTextBox().Text = "30";
        editor.FindButton("AddPeriodButton").Invoke(); // Monday 9:00 AM–5:00 PM defaults.
        editor.FindButton("Save").Invoke();
        Assert.True(SpinWait.SpinUntil(() => fixture.OpenDatabase().GetRules().Count == 1, TimeSpan.FromSeconds(3)));
        var rule = Assert.Single(fixture.OpenDatabase().GetRules());
        Assert.Equal(90, rule.GetScheduleForDay(DayOfWeek.Monday).DailyLimitMinutes);
        var period = Assert.Single(rule.BlockedPeriods);
        Assert.Equal(540, period.StartMinute); Assert.Equal(1020, period.EndMinute);
        settings.FindButton("Save").Invoke();
        Signal(fixture.Runtime.DashboardEventName);
        var dashboard = fixture.App.WaitForWindow(fixture.Automation, "ScreenTime — Main");
        var dashboardHandle = dashboard.Properties.NativeWindowHandle.Value;
        Signal(fixture.Runtime.DashboardEventName); Thread.Sleep(200);
        Assert.Equal(dashboardHandle, Assert.Single(fixture.App.GetAllTopLevelWindows(fixture.Automation).Where(w => w.Title.Contains("ScreenTime — Main"))).Properties.NativeWindowHandle.Value);
        Signal(fixture.Runtime.StatusEventName);
        FlaUI.Core.AutomationElements.Window? popup = null;
        Assert.True(SpinWait.SpinUntil(() => (popup = fixture.App.GetAllTopLevelWindows(fixture.Automation).SingleOrDefault(w => w.Title == "ScreenTime")) is not null, TimeSpan.FromSeconds(3)));
        popup!.FindButton("Dashboard").Invoke();
        Assert.True(SpinWait.SpinUntil(() => !fixture.App.GetAllTopLevelWindows(fixture.Automation).Any(w => w.Title == "ScreenTime"), TimeSpan.FromSeconds(3)));
        dashboard.Close(); Assert.False(fixture.App.HasExited);
        fixture.StopApplication();
    }
    [ReleaseFact]
    public void ExtractedPackage_Resources_ProtectedCommands_DuplicateAndCleanExit()
    {
        using var fixture = new ReleaseFixture();
        using (var process = Process.GetProcessById(fixture.App.ProcessId))
        {
            Assert.Equal(Path.GetFullPath(Executable), process.MainModule!.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (var module in process.Modules.Cast<ProcessModule>().Where(m => new[] { "coreclr.dll", "e_sqlite3.dll" }.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase)))
                Assert.StartsWith(Path.GetDirectoryName(Executable)!, module.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(process.Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(process.Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
        }
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        info.ArgumentList.Add("--test-profile"); info.ArgumentList.Add(fixture.Runtime.Paths.Root);
        info.Environment.Remove("TIMEGUARD_TEST_DB");
        using (var duplicate = Process.Start(info)!)
        {
            var identity = OwnedProcessIdentity.Capture(duplicate);
            try { Assert.True(duplicate.WaitForExit(5000)); Assert.Equal(0, duplicate.ExitCode); }
            finally { identity.Terminate(); }
        }
        Assert.False(fixture.App.HasExited);
        Signal(fixture.Runtime.StatusEventName);
        Window? panel = null;
        var ready = SpinWait.SpinUntil(() =>
        {
            if (fixture.App.HasExited) return false;
            panel = fixture.App.GetAllTopLevelWindows(fixture.Automation).SingleOrDefault(w =>
                w.Title == "ScreenTime" && w.Properties.ProcessId.ValueOrDefault == fixture.App.ProcessId);
            return panel?.FindTextContaining("No enabled applications configured") is not null;
        }, TimeSpan.FromSeconds(5));
        var windowState = $"fixture PID={fixture.App.ProcessId}; exited={fixture.App.HasExited}; " +
            string.Join(" | ", fixture.App.GetAllTopLevelWindows(fixture.Automation).Select(w =>
                $"title='{w.Title}', HWND={w.Properties.NativeWindowHandle.Value}, PID={w.Properties.ProcessId.Value}, content=[{string.Join("; ", w.FindAllDescendants().Select(e => e.Name))}]"));
        output.WriteLine("Package popup readiness: " + windowState);
        Assert.True(ready, "The exact fixture popup must render its empty state without reactivation or reopening. " + windowState);
        Assert.NotNull(panel!.FindTextContaining("No enabled applications configured")); panel!.Close();
        var before = System.Text.Json.JsonSerializer.Serialize(fixture.OpenDatabase().LoadConfig());
        foreach (var signal in new[] { fixture.Runtime.SettingsEventName, fixture.Runtime.ExitEventName })
        {
            Signal(signal); var prompt = fixture.App.WaitForWindow(fixture.Automation, "Protected Access");
            Password(prompt, "incorrect");
            Assert.True(SpinWait.SpinUntil(() => prompt.FindTextContaining("Incorrect password") is not null, TimeSpan.FromSeconds(3)));
            prompt.Close(); Assert.False(fixture.App.HasExited);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fixture.OpenDatabase().LoadConfig()));
        }
        fixture.RequestSettings(); Password(fixture.App.WaitForWindow(fixture.Automation, "Protected Access"), AppFixture.TestPassword);
        var settings = fixture.App.WaitForWindow(fixture.Automation, "ScreenTime — Main");
        settings.FindButton("Cancel").Invoke();
        Signal(fixture.Runtime.ExitEventName); Password(fixture.App.WaitForWindow(fixture.Automation, "Protected Access"), AppFixture.TestPassword);
        Assert.True(SpinWait.SpinUntil(() => fixture.App.HasExited, TimeSpan.FromSeconds(5)));
    }

    [ReleaseFact]
    public async Task ExtractedPackage_ActualGrant_RelaunchDenied_RestartKeepsOriginalTwentyMinutes()
    {
        using var fixture = new GrantFixture(); using var other = new ReleaseFixture();
        var unrelated = other.LaunchHelper(headless: true); var original = fixture.LaunchHelper(headless: true);
        var db = fixture.OpenDatabase();
        await Until(() => db.LoadGraceEpisodes().Count == 1, 15);
        var episode = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(GraceEpisode.DefaultDuration, episode.ExpiresAtUtc - episode.StartedAtUtc);
        Assert.Equal(original.Id, Assert.Single(episode.Processes).ProcessId); Assert.True(Alive(original));
        var replacement = fixture.LaunchHelper(headless: true); await Until(() => !Alive(replacement));
        fixture.RestartApplication();
        var recovered = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(episode.Id, recovered.Id); Assert.Equal(episode.ExpiresAtUtc, recovered.ExpiresAtUtc);
        Assert.Equal(episode.Processes, recovered.Processes); Assert.True(Alive(original)); Assert.True(Alive(unrelated));
        var samples = 0;
        if (Environment.GetEnvironmentVariable("SCREENTIME_RELEASE_FULL_DURATION") == "1")
        {
            while (DateTimeOffset.UtcNow < episode.ExpiresAtUtc.AddMilliseconds(-250))
            {
                Assert.True(Alive(original)); Assert.True(Alive(unrelated));
                Assert.Equal(GracePhase.Active, Assert.Single(db.LoadGraceEpisodes()).Phase); samples++;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, (episode.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalMilliseconds - 250))));
            }
            await Until(() => !Alive(original), 5);
            var expired = Assert.Single(db.LoadGraceEpisodes());
            Assert.Equal(GracePhase.Expired, expired.Phase); Assert.Equal(episode.ExpiresAtUtc, expired.EndedAtUtc);
            Assert.Equal(episode.ExpiresAtUtc, expired.ExpiresAtUtc); Assert.True(Alive(unrelated));
            output.WriteLine($"Actual packaged 20-minute gate: Started={episode.StartedAtUtc:O}; Deadline={episode.ExpiresAtUtc:O}; Samples={samples}; Completed={DateTimeOffset.UtcNow:O}");
        }
        else output.WriteLine("Actual package grant/restart checked; elapsed twenty-minute expiry not selected in this run.");
        fixture.StopApplication(); other.StopApplication();
    }

    [ReleaseFact]
    public async Task ExtractedPackage_SeededOriginalTwentyMinuteEpisode_ExpiresWithoutRenewal()
    {
        using var fixture = new ReleaseFixture(); using var other = new ReleaseFixture();
        fixture.StopApplication();
        var original = fixture.LaunchHelper(headless: true); var unrelated = other.LaunchHelper(headless: true);
        using var process = Process.GetProcessById(original.Id);
        var db = fixture.OpenDatabase();
        var date = DateOnly.FromDateTime(DateTime.Now);
        db.SaveRule(new() { ProcessName = "screentime.testprocess", DailyLimitMinutes = 1 });
        db.UpsertUsageEntry(date, new() { ProcessName = "screentime.testprocess", QuotaSeconds = 60, ObservedSeconds = 60 });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var episode = new GraceEpisode(Guid.NewGuid().ToString("N"), "screentime.testprocess", date,
            deadline - GraceEpisode.DefaultDuration, deadline, GracePhase.Active, null,
            [new("screentime.testprocess", original.Id, original.StartTimeUtcTicks, process.SessionId)]);
        db.CommitObservation([], [episode]); fixture.RestartApplication();
        Assert.True(Alive(original)); Assert.Equal(deadline, Assert.Single(db.LoadGraceEpisodes()).ExpiresAtUtc);
        var replacement = fixture.LaunchHelper(headless: true); await Until(() => !Alive(replacement));
        while (DateTimeOffset.UtcNow < deadline.AddMilliseconds(-250))
        {
            Assert.True(Alive(original)); Assert.True(Alive(unrelated));
            Assert.Equal(GracePhase.Active, Assert.Single(db.LoadGraceEpisodes()).Phase);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds - 250))));
        }
        await Until(() => !Alive(original), 5);
        var expired = Assert.Single(db.LoadGraceEpisodes()); Assert.Equal(GracePhase.Expired, expired.Phase);
        Assert.Equal(deadline, expired.ExpiresAtUtc); Assert.Equal(deadline, expired.EndedAtUtc); Assert.True(Alive(unrelated));
        fixture.RestartApplication();
        var afterExpiry = Assert.Single(db.LoadGraceEpisodes());
        Assert.Equal(episode.Id, afterExpiry.Id); Assert.Equal(deadline, afterExpiry.ExpiresAtUtc);
        Assert.Equal(GracePhase.Expired, afterExpiry.Phase);
        var later = fixture.LaunchHelper(headless: true); await Until(() => !Alive(later));
        Assert.Equal(episode.Id, Assert.Single(db.LoadGraceEpisodes()).Id); Assert.True(Alive(unrelated));
        fixture.StopApplication(); other.StopApplication();
        output.WriteLine("Seeded near-expiry recovery of a valid original20minute episode; not an actual elapsed20minute run.");
    }
}
