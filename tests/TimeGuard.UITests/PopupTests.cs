using System.Diagnostics;
using TimeGuard.UITests.Helpers;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>
/// Tests the BlockedPopup window.
/// The fixture seeds a "screentime.testprocess" rule that is already over-limit, then starts
/// the owned helper so MonitorService detects it running and fires BlockRequested.
/// </summary>
public class PopupTests : IClassFixture<PopupTestFixture>
{
    private readonly PopupTestFixture _fx;

    public PopupTests(PopupTestFixture fx) => _fx = fx;

    [Fact]
    public void BlockedPopup_ShowsCorrectTitle()
    {
        _fx.EnsureHelperRunning();
        var popup = _fx.App.WaitForWindow(_fx.Automation, "Time's Up",
            timeout: TimeSpan.FromSeconds(20));
        Assert.Contains("Time", popup.Title);
        Assert.True(_fx.HelperExited());
        popup.FindButton("OK").Click(); // close so the next test starts clean
        Thread.Sleep(300);
    }

    [Fact]
    public void BlockedPopup_OkButton_ClosesPopup()
    {
        _fx.EnsureHelperRunning();
        var popup = _fx.App.WaitForWindow(_fx.Automation, "Time's Up",
            timeout: TimeSpan.FromSeconds(20));
        popup.FindButton("OK").Click();
        Thread.Sleep(500);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Time's Up") == true),
            "BlockedPopup should close after clicking OK.");
    }
}

/// <summary>
/// Fixture that seeds a "screentime.testprocess" rule already over-limit.
/// Call <see cref="EnsureHelperRunning"/> in each test so the monitor detects the process.
/// </summary>
public class PopupTestFixture : AppFixture
{
    private OwnedProcessIdentity? _helper;

    protected override void SeedDatabase()
    {
        var (hash, salt) = HashPassword(TestPassword);
        SaveConfigToDb(hash, salt);

        var db = OpenDb();
        db.SaveRule(new TimeGuard.Models.AppRule
        {
            ProcessName       = "screentime.testprocess",
            DisplayName       = "Test helper",
            DailyLimitMinutes = 1,
            Enabled           = true
        });

        // Already 2 minutes over the 1-minute limit
        db.UpsertUsageEntry(DateOnly.FromDateTime(DateTime.Today),
            new TimeGuard.Models.UsageEntry
            {
                ProcessName  = "screentime.testprocess",
                UsageMinutes = 2.0,
                Blocked      = false,
                WarningSent  = false
            });
    }

    /// <summary>Launch a fresh owned helper; the app validates its identity before termination.</summary>
    public void EnsureHelperRunning()
    {
        _helper = LaunchHelper();
    }

    public bool HelperExited()
    {
        if (_helper is null) return false;
        try
        {
            using var process = Process.GetProcessById(_helper.Id);
            return !_helper.Matches(process);
        }
        catch (ArgumentException) { return true; }
    }
}
