using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using TimeGuard.UITests.Helpers;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>Each test starts with a fresh profile and exercises every popup from its owned launch.</summary>
public class PopupTests : IDisposable
{
    private readonly PopupTestFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void BlockedPopup_ShowsCorrectTitle()
    {
        _fx.EnsureHelperRunning();
        var popups = _fx.WaitForInitialBlockPopups();
        foreach (var popup in popups)
            Assert.Contains("Time", popup.Title);
        Assert.True(_fx.HelperExited());
        ClosePopupsThroughOk(popups);
    }

    [Fact]
    public void BlockedPopup_OkButton_ClosesPopup()
    {
        _fx.EnsureHelperRunning();
        var popups = _fx.WaitForInitialBlockPopups();
        ClosePopupsThroughOk(popups);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.False(windows.Any(w => w.Title?.Contains("Time's Up") == true),
            "BlockedPopup should close after clicking OK.");
    }

    [Fact]
    public void ClosingPopup_DoesNotPermitAnOverQuotaRelaunch()
    {
        _fx.EnsureHelperRunning();
        ClosePopupsThroughOk(_fx.WaitForInitialBlockPopups());
        Assert.True(_fx.HelperExited());
        _fx.EnsureHelperRunning();
        var popups = _fx.WaitForInitialBlockPopups();
        Assert.True(_fx.HelperExited());
        ClosePopupsThroughOk(popups);
    }

    private void ClosePopupsThroughOk(Window[] popups)
    {
        foreach (var popup in popups)
        {
            var handle = popup.Properties.NativeWindowHandle.Value;
            // Invoke the actual button: overlapping topmost windows can redirect a coordinate click.
            popup.FindButton("OK").Invoke();
            Assert.True(SpinWait.SpinUntil(() =>
                !_fx.App.GetAllTopLevelWindows(_fx.Automation)
                    .Any(w => w.Properties.NativeWindowHandle.Value == handle), TimeSpan.FromSeconds(5)),
                "BlockedPopup should close after clicking OK.");
        }
    }
}

/// <summary>Seeds an over-limit rule; only the fixture-owned helper is eligible for termination.</summary>
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

    public Window[] WaitForInitialBlockPopups()
    {
        Window[] popups = [];
        // One decision produces one popup; the old duplicate relaunch pass is gone.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            popups = App.GetAllTopLevelWindows(Automation).Where(w => w.Title == "Time's Up").ToArray();
            return popups.Length == 1 && popups.All(w =>
                w.FindFirstDescendant(cf => cf.ByName("OK")) is { IsEnabled: true, IsOffscreen: false });
        }, TimeSpan.FromSeconds(20)), "Expected one initial-block popup and their enabled OK buttons.");
        return popups;
    }

    /// <summary>Launch a fresh owned helper; the app validates its identity before termination.</summary>
    public void EnsureHelperRunning() => _helper = LaunchHelper();

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
