using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.UITests;

public class NotificationWindowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class PreviewFixture : AppFixture { protected override bool PreviewNotices => true; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [Fact]
    public void PassiveNotices_PreserveForegroundAndKeyboard_PassMouseThrough_AutoDismiss_AllKinds()
    {
        using var fx = new PreviewFixture();
        var helper = fx.LaunchHelper(inputProbe: true);
        using var helperApp = FlaUI.Core.Application.Attach(helper.Id);
        var window = helperApp.GetMainWindow(fx.Automation, TimeSpan.FromSeconds(5));
        Assert.NotNull(window);
        window.Focus();
        var helperHwnd = window.Properties.NativeWindowHandle.Value;
        Assert.True(SpinWait.SpinUntil(() => GetForegroundWindow() == helperHwnd, TimeSpan.FromSeconds(5)),
            "Interactive desktop unavailable: the owned helper cannot become foreground.");
        var path = Path.Combine(fx.Runtime.Paths.Root, "notice-diagnostics.jsonl");
        var inputPath = Path.Combine(fx.Runtime.Paths.Root, "helper-input.txt");
        using var signal = EventWaitHandle.OpenExisting(fx.Runtime.NoticeEventName);
        for (var kind = 0; kind < 5; kind++)
        {
            Assert.Equal(helperHwnd, GetForegroundWindow());
            signal.Set();
            Window? notice = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                notice = fx.App.GetAllTopLevelWindows(fx.Automation).SingleOrDefault(w => w.Title == "ScreenTime notice");
                return notice is not null;
            }, TimeSpan.FromSeconds(5)));
            var hwnd = notice!.Properties.NativeWindowHandle.Value;
            Assert.NotEqual(helperHwnd, hwnd);
            Assert.Equal(helperHwnd, GetForegroundWindow());
            Assert.Empty(notice.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)));
            Assert.False(notice.Properties.IsKeyboardFocusable.Value);
            Assert.Equal(new IntPtr(3), SendMessage(hwnd, 0x21, helperHwnd, IntPtr.Zero));
            var bounds = notice.BoundingRectangle;
            var point = new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            var packed = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            Assert.Equal(new IntPtr(-1), SendMessage(hwnd, 0x84, IntPtr.Zero, packed));
            var before = ReadCounts(inputPath);
            output.WriteLine($"Kind={kind} notice={hwnd} helper={helperHwnd} bounds={bounds} before={before} foreground={GetForegroundWindow()}");
            // Real cross-process input through the opaque part of the notice, not a synthetic click message.
            Mouse.Click(point);
            Keyboard.Press(VirtualKeyShort.F8);
            Keyboard.Release(VirtualKeyShort.F8);
            var received = SpinWait.SpinUntil(() =>
            {
                var after = ReadCounts(inputPath);
                return after.Clicks > before.Clicks && after.Keys > before.Keys;
            }, TimeSpan.FromSeconds(2));
            output.WriteLine($"After={ReadCounts(inputPath)} foreground={GetForegroundWindow()}");
            if (!received)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
                output.WriteLine($"Foreground process ID={foregroundPid}; owned helper process ID={helper.Id}");
                output.WriteLine(string.Join(Environment.NewLine, File.ReadAllLines(path).TakeLast(4)));
            }
            Assert.True(received, "Owned helper must receive both mouse and keyboard input through the notice.");
            Assert.Equal(helperHwnd, GetForegroundWindow());
            Assert.False(IsIconic(helperHwnd));
            Assert.True(SpinWait.SpinUntil(() => !fx.App.GetAllTopLevelWindows(fx.Automation)
                .Any(w => w.Properties.NativeWindowHandle.Value == hwnd), TimeSpan.FromSeconds(8)));
        }
        var rows = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<Sample>(line)!).ToArray();
        Assert.Equal(5, rows.Count(r => r.Stage == "Shown"));
        Assert.Equal(5, rows.Count(r => r.Stage == "Closed"));
        foreach (var row in rows.Where(r => r.Stage != "Closed"))
        {
            Assert.Equal(helperHwnd.ToInt64(), row.ForegroundBefore);
            Assert.Equal(helperHwnd.ToInt64(), row.Foreground);
            Assert.NotEqual(row.Hwnd, row.Active);
            Assert.NotEqual(row.Hwnd, row.Focus);
            Assert.False(row.KeyboardFocusWithin);
            Assert.False(row.MouseCaptured);
            const int expected = 0x08000000 | 0x80 | 0x20 | 0x80000;
            Assert.Equal(expected, row.ExtendedStyles & expected);
        }
        // HWND values can be reused; pair each sequential show/close rather than grouping by HWND.
        var shows = rows.Where(r => r.Stage == "Shown").ToArray();
        var closes = rows.Where(r => r.Stage == "Closed").ToArray();
        for (var i = 0; i < shows.Length; i++) Assert.InRange((closes[i].AtUtc - shows[i].AtUtc).TotalSeconds, 5, 8);
        var artifact = Path.Combine(AppContext.BaseDirectory, $"notice-samples-{Guid.NewGuid():N}.jsonl");
        File.Copy(path, artifact);
        output.WriteLine($"Verified {rows.Length} samples; durations: {string.Join(", ", shows.Select((s, i) => (closes[i].AtUtc - s.AtUtc).TotalSeconds.ToString("F3")))}. Artifact: {artifact}");
        Assert.False(File.Exists(fx.Runtime.Paths.DatabasePath)); // Preview has no policy/store/enforcement path.
    }

    private static (int Clicks, int Keys) ReadCounts(string path)
    {
        try
        {
            // The helper writes on input. A reader denying write-sharing can cause the
            // helper's IOException dialog to steal foreground, invalidating the experiment.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var parts = reader.ReadToEnd().Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out var clicks) && int.TryParse(parts[1], out var keys)
                ? (clicks, keys) : (-1, -1);
        }
        catch (IOException) { return (-1, -1); }
    }

    private sealed record Sample(string Stage, DateTimeOffset AtUtc, long Hwnd, long ForegroundBefore,
        long Foreground, long Active, long Focus, bool KeyboardFocusWithin, bool MouseCaptured, int ExtendedStyles);
}
