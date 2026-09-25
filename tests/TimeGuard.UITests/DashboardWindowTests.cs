using TimeGuard.UITests.Helpers;
using Xunit;

namespace TimeGuard.UITests;

/// <summary>
/// Tests the Dashboard window (7-day bar chart + drilldown table).
/// Uses <see cref="SeededAppFixture"/> to skip first-run, then opens Settings
/// and clicks the Dashboard button.
/// </summary>
public class DashboardWindowTests : IClassFixture<SeededAppFixture>
{
    private readonly SeededAppFixture _fx;

    public DashboardWindowTests(SeededAppFixture fx) => _fx = fx;

    private FlaUI.Core.AutomationElements.Window OpenDashboard()
    {
        // Open settings the same way SettingsWindowTests does
        _fx.RequestSettings();

        var prompt = _fx.App.WaitForWindow(_fx.Automation, "Protected Access");
        var boxes = prompt.FindAllDescendants(cf => cf.ByFrameworkId("WPF")
            .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));
        if (boxes.Length > 0)
        {
            boxes[0].Focus();
            FlaUI.Core.Input.Keyboard.Type(AppFixture.TestPassword);
        }
        prompt.FindButton("Unlock").Click();

        var settings = _fx.App.WaitForWindow(_fx.Automation, "ScreenTime Settings");
        settings.FindButton("📊 Dashboard").Click();

        return _fx.App.WaitForWindow(_fx.Automation, "Usage Dashboard");
    }

    [Fact]
    public void Dashboard_HasCorrectTitle()
    {
        var dashboard = OpenDashboard();
        Assert.Contains("Dashboard", dashboard.Title);
        dashboard.Close();
        _fx.App.WaitForWindow(_fx.Automation, "ScreenTime Settings").Close();
    }

    [Fact]
    public void Dashboard_DrilldownHeaderIsPresent()
    {
        var dashboard = OpenDashboard();

        // The drilldown header TextBlock is always present (default text or a date after click)
        var header = dashboard.FindAllDescendants()
            .FirstOrDefault(e => e.Name?.Contains("Click a bar") == true
                              || e.Name?.Contains("breakdown") == true);
        Assert.NotNull(header);
        dashboard.Close();
        _fx.App.WaitForWindow(_fx.Automation, "ScreenTime Settings").Close();
    }
}
