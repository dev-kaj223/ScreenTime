using System.Diagnostics;
using TimeGuard.Models;
using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

public class DashboardWindowTests
{
    private sealed class PopulatedFixture : SeededAppFixture
    {
        protected override void SeedDatabase()
        {
            base.SeedDatabase();
            var db = OpenDatabase();
            db.SaveRule(new() { ProcessName = "screentime.testprocess", DisplayName = "Dashboard helper", DailyLimitMinutes = 1 });
            db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Today), new() { ProcessName = "screentime.testprocess", QuotaSeconds = 60, ObservedSeconds = 60 });
            db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Today).AddDays(-2), new() { ProcessName = "screentime.testprocess", QuotaSeconds = 30, ObservedSeconds = 30 });
            db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Today), new() { ProcessName = "unrelated-legacy", QuotaSeconds = 600, ObservedSeconds = 600 });
        }
    }
    private static void Open(AppFixture fx) { using var signal = EventWaitHandle.OpenExisting(fx.Runtime.DashboardEventName); signal.Set(); }

    [Fact]
    public void Dashboard_PasswordFree_Singleton_EmptyState_CloseDoesNotExit()
    {
        using var fx = new SeededAppFixture();
        Open(fx);
        var dashboard = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
        Assert.NotNull(dashboard.FindTextContaining("No usage recorded yet."));
        Assert.NotNull(dashboard.FindTextContaining("Last 7 Days"));
        dashboard.FindButton("Month").Invoke();
        Assert.NotNull(dashboard.FindTextContaining("Last 30 Days"));
        Assert.NotNull(dashboard.FindTextContaining("last thirty days"));
        dashboard.FindButton("Year").Invoke();
        Assert.NotNull(dashboard.FindTextContaining("Last 12 Months"));
        Assert.NotNull(dashboard.FindTextContaining("last twelve months"));
        dashboard.FindButton("Week").Invoke();
        Assert.NotNull(dashboard.FindTextContaining("Last 7 Days"));
        Assert.DoesNotContain(fx.App.GetAllTopLevelWindows(fx.Automation), w => w.Title == "Protected Access");
        var handle = dashboard.Properties.NativeWindowHandle.Value;
        TrayStatusTests.AssertFullyOnscreen(dashboard);
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var bounds = dashboard.BoundingRectangle;
        Assert.InRange(Math.Abs((bounds.Left + bounds.Right) / 2 - (area.Left + area.Right) / 2), 0, 4);
        Assert.InRange(Math.Abs((bounds.Top + bounds.Bottom) / 2 - (area.Top + area.Bottom) / 2), 0, 4);
        Open(fx); Thread.Sleep(300);
        Assert.Equal(handle, Assert.Single(fx.App.GetAllTopLevelWindows(fx.Automation).Where(w => w.Title.Contains("ScreenTime — Main"))).Properties.NativeWindowHandle.Value);
        dashboard.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-dashboard-empty.png"));
        dashboard.Close(); Assert.False(fx.App.HasExited);
        Open(fx); fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main").Close();
    }

    [Fact]
    public void Dashboard_PopulatedTodayAndHistory_CloseLeavesExactEnforcementRunning()
    {
        using var fx = new PopulatedFixture();
        Open(fx);
        var dashboard = fx.App.WaitForWindow(fx.Automation, "ScreenTime — Main");
        Assert.NotNull(dashboard.FindTextContaining("Dashboard helper"));
        Assert.NotNull(dashboard.FindTextContaining("DAILY LIMIT REACHED"));
        Assert.NotNull(dashboard.FindTextContaining("1 min used / 1 min"));
        Assert.Null(dashboard.FindTextContaining("No usage recorded yet."));
        Assert.Null(dashboard.FindTextContaining("unrelated-legacy"));
        Assert.NotNull(dashboard.FindTextContaining("Click a bar"));
        var before = System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadLog(DateOnly.FromDateTime(DateTime.Today)));
        dashboard.FindButton("Month").Invoke();
        Assert.NotNull(dashboard.FindTextContaining("Last 30 Days"));
        dashboard.FindButton("Year").Invoke();
        Assert.NotNull(dashboard.FindTextContaining("Last 12 Months"));
        Assert.Null(dashboard.FindTextContaining("Click a bar"));
        dashboard.FindButton("Week").Invoke();
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(fx.OpenDatabase().LoadLog(DateOnly.FromDateTime(DateTime.Today))));
        dashboard.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "beta2-dashboard-populated.png"));
        dashboard.Close();
        var helper = fx.LaunchHelper(headless: true);
        using var process = Process.GetProcessById(helper.Id);
        Assert.True(process.WaitForExit(8000)); Assert.False(fx.App.HasExited);
    }
}
