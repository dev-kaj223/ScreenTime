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
    public void BlockedNotice_ShowsCorrectTitle_AndEnforces()
    {
        _fx.EnsureHelperRunning();
        var popups = _fx.WaitForInitialBlockPopups();
        foreach (var popup in popups)
            Assert.Equal("ScreenTime notice", popup.Title);
        Assert.True(_fx.HelperExited());
        WaitForAutomaticDismissal(popups);
    }

    [Fact]
    public void BlockedNotice_HasNoButton_AndDismissesWithoutInteraction()
    {
        _fx.EnsureHelperRunning();
        var popups = _fx.WaitForInitialBlockPopups();
        foreach (var popup in popups)
            Assert.Empty(popup.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)));
        WaitForAutomaticDismissal(popups);

        var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
        Assert.DoesNotContain(windows, w => w.Title == "ScreenTime notice");
    }

    [Fact]
    public void ClosingPopup_DoesNotPermitAnOverQuotaRelaunch()
    {
        _fx.EnsureHelperRunning();
        WaitForAutomaticDismissal(_fx.WaitForInitialBlockPopups());
        Assert.True(_fx.HelperExited());
        _fx.EnsureHelperRunning();
        Assert.True(SpinWait.SpinUntil(_fx.HelperExited, TimeSpan.FromSeconds(12)));
        // The optional blocked notice is rate-limited; suppression never permits relaunch.
    }

    private void WaitForAutomaticDismissal(Window[] popups)
    {
        foreach (var popup in popups)
        {
            var handle = popup.Properties.NativeWindowHandle.Value;
            Assert.True(SpinWait.SpinUntil(() =>
                !_fx.App.GetAllTopLevelWindows(_fx.Automation)
                    .Any(w => w.Properties.NativeWindowHandle.Value == handle), TimeSpan.FromSeconds(8)),
                "Passive notice must close without input.");
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
            popups = App.GetAllTopLevelWindows(Automation).Where(w => w.Title == "ScreenTime notice").ToArray();
            return popups.Length == 1;
        }, TimeSpan.FromSeconds(20)), "Expected one passive blocked notice.");
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
