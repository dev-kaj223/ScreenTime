using FlaUI.Core.Input;
using TimeGuard.UITests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace TimeGuard.UITests;

/// <summary>
/// Tests the FirstRunWindow that appears when the app has no password configured.
/// Each test creates its own AppFixture (fresh blank DB) so ordering never matters.
/// </summary>
public class FirstRunWindowTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    public FirstRunWindowTests(ITestOutputHelper output) => _output = output;
    private readonly AppFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void App_ShowsFirstRunWindow_OnFreshDb()
    {
        var win = _fx.App.WaitForWindow(_fx.Automation, "Welcome to ScreenTime");
        Assert.Contains("Welcome", win.Title);
    }

    [Fact]
    public void FirstRun_MismatchedPasswords_ShowsError()
    {
        var win = _fx.App.WaitForWindow(_fx.Automation, "Welcome to ScreenTime");

        // Type mismatched passwords using keyboard — PasswordBoxes don't expose Text via UIA
        var boxes = win.FindAllDescendants(cf =>
            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));

        if (boxes.Length >= 2)
        {
            boxes[0].Click();
            Keyboard.Type("abc123");
            boxes[1].Click();
            Keyboard.Type("different");
        }

        win.FindButton("Get Started →").Click();
        Thread.Sleep(300);

        // ErrorText becomes visible on mismatch
        var errorEl = win.FindAllDescendants()
            .FirstOrDefault(e => e.Name?.Contains("match", StringComparison.OrdinalIgnoreCase) == true
                              || e.Name?.Contains("do not", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(errorEl);
    }

    [Fact]
    public void FirstRun_ValidPassword_ClosesFirstRunWindow()
    {
        var win = _fx.App.WaitForWindow(_fx.Automation, "Welcome to ScreenTime");

        var boxes = win.FindAllDescendants(cf =>
            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));

        Assert.True(boxes.Length >= 2, $"Expected 2+ PasswordBox controls, found {boxes.Length}.");

        foreach (var box in boxes.Take(2))
        {
            // UIA focus is explicit and verified before sending keyboard input; a coordinate
            // click plus a fixed sleep does not establish where the password will be typed.
            box.Focus();
            Assert.True(SpinWait.SpinUntil(() => box.Properties.HasKeyboardFocus.Value,
                TimeSpan.FromSeconds(5)), "Password field did not receive keyboard focus.");
            Keyboard.Type(AppFixture.TestPassword);
            _fx.App.WaitWhileBusy(TimeSpan.FromSeconds(5));
        }

        var startButton = win.FindButton("Get Started →");
        Assert.True(SpinWait.SpinUntil(() => startButton.IsEnabled && !startButton.IsOffscreen,
            TimeSpan.FromSeconds(5)), "Get Started button did not become ready.");
        startButton.Invoke();

        // Poll until the FirstRunWindow disappears (up to 5s)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool closed = false;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            try
            {
                var windows = _fx.App.GetAllTopLevelWindows(_fx.Automation);
                closed = !windows.Any(w =>
                    w.Title?.Contains("Welcome to ScreenTime", StringComparison.OrdinalIgnoreCase) == true);
                if (closed) break;
            }
            catch { /* process may be transitioning */ }
        }

        if (!closed)
            _output.WriteLine("Validation: " + win.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText"))?.Name);
        Assert.True(closed, "FirstRunWindow should have closed after valid password setup.");
        var settings = _fx.App.WaitForWindow(_fx.Automation, "ScreenTime — Main");
        Assert.DoesNotContain(_fx.App.GetAllTopLevelWindows(_fx.Automation), w => w.Title == "Protected Access");
        Assert.NotNull(settings.FindButton("Settings 🔓"));
        Assert.NotNull(settings.FindButton("➕ Add Rule"));
        settings.Close();
        Assert.True(SpinWait.SpinUntil(() => !_fx.App.GetAllTopLevelWindows(_fx.Automation).Any(w => w.Title == "ScreenTime — Main"), TimeSpan.FromSeconds(3)));
        _fx.RequestSettings();
        var prompt = _fx.App.WaitForWindow(_fx.Automation, "Protected Access");
        prompt.Close();
        Assert.False(_fx.App.HasExited);
    }
}
