using System.Windows;
using System.Windows.Threading;
using TimeGuard.Helpers;
using TimeGuard.Services;
using WpfApplication = System.Windows.Application;

namespace TimeGuard;

public partial class App : WpfApplication
{
    private DatabaseService? _db;
    private MonitorService? _monitor;
    private PowerSessionHelper? _power;
    private GlobalHotkeyHelper? _hotkey;
    private Window? _helperWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private RuntimeOptions _runtime = RuntimeOptions.Development();
    private IAppLogger? _logger;
    private bool _stopping;
    private EventWaitHandle? _settingsEvent;
    private EventWaitHandle? _stopEvent;
    private DispatcherTimer? _testCommands;
    private WpfNotificationService? _notifications;
    private EventWaitHandle? _noticeEvent;
    private TimeGuard.Models.NotificationRequest? _previewNotice;
    private int _previewIndex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Install last-resort reporting before storage/migration and UI initialization.
        DispatcherUnhandledException += (_, args) =>
            _logger.TryWrite("Critical", "DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.TryWrite("Critical", "AppDomainUnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            _logger.TryWrite("Error", "UnobservedTaskException", args.Exception);

        try
        {
            _runtime = RuntimeOptions.Resolve(e.Args, Environment.GetEnvironmentVariable("TIMEGUARD_TEST_DB"));
            _logger = new JsonFileLogger(_runtime);
            _singleInstanceMutex = new Mutex(true, _runtime.MutexName, out _ownsMutex);
            if (!_ownsMutex) { Shutdown(); return; }
            _logger.TryWrite("Information", "ApplicationStarting");
            if (e.Args.Contains("--preview-notices"))
            {
                if (_runtime.Profile != RuntimeProfile.Test)
                    throw new ArgumentException("Notice preview requires an isolated --test-profile.");
                // Deliberately no database, monitor, process discovery or enforcement in preview.
                StartNoticePreview();
                return;
            }
            _db = new DatabaseService(_runtime.Paths);
            var config = _db.LoadConfig();
            if (config.IsFirstRun)
            {
                var setup = new UI.FirstRunWindow(_db);
                if (setup.ShowDialog() != true) { Shutdown(); return; }
                config = _db.LoadConfig();
            }

            if (!StartupHelper.IsRegistered(_runtime)) StartupHelper.Register(_runtime);
            Func<TimeGuard.Models.ProcessInstance, bool>? targetScope = _runtime.Profile == RuntimeProfile.Test
                ? new TestProcessScope(_runtime.Paths, _logger).Contains : null;
            _monitor = new MonitorService(_db, new RulesEngine(), config, _logger,
                new WindowsProcessMonitor(_logger, targetScope), new WindowsProcessTerminator(targetScope));
            _notifications = new WpfNotificationService(
                () => _monitor.TryReadNotification(out var request) ? request : null,
                _monitor.IsNotificationCurrent, _logger,
                e.Args.Contains("--notice-diagnostics") ? WriteNoticeDiagnostic : null);

            _power = new PowerSessionHelper(_monitor);
            _monitor.Start();
            ObserveMonitorAsync();

            if (_runtime.Profile == RuntimeProfile.Test)
            {
                // Local named signals scoped to this profile replace global keyboard input in tests.
                _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _runtime.SettingsEventName);
                _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _runtime.StopEventName);
                _testCommands = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _testCommands.Tick += async (_, _) =>
                {
                    if (_stopEvent.WaitOne(0)) await StopAndShutdownAsync(0);
                    else if (!_stopping && _settingsEvent.WaitOne(0))
                        _ = Dispatcher.BeginInvoke(new Action(OpenSettings));
                };
                _testCommands.Start();
            }
            else
            {
                _helperWindow = new Window
                {
                    Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false
                };
                _helperWindow.Show();
                _helperWindow.Hide();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_helperWindow).Handle;
                try
                {
                    var hotkey = _runtime.Profile == RuntimeProfile.Development
                        ? "Ctrl+Alt+Shift+S" : config.SettingsHotkey;
                    _hotkey = new GlobalHotkeyHelper(hwnd, 1, hotkey, OpenSettings);
                }
                catch (Exception ex) { _logger.TryWrite("Error", "HotkeyRegistrationFailed", ex); }
            }
        }
        catch (Exception ex)
        {
            _logger ??= new JsonFileLogger(_runtime);
            _logger.TryWrite("Critical", "ApplicationStartupFailed", ex);
            Shutdown(1);
        }
    }

    private async void ObserveMonitorAsync()
    {
        try { await _monitor!.Completion; }
        catch (Exception) { await StopAndShutdownAsync(1); }
    }

    private async Task StopAndShutdownAsync(int exitCode)
    {
        if (_stopping) return;
        _stopping = true;
        _testCommands?.Stop();
        _notifications?.Dispose();
        _power?.Dispose();
        _monitor?.Dispose();
        if (_monitor is not null)
        {
            try { await _monitor.StopAsync(); }
            catch (Exception) { exitCode = 1; } // Already logged by supervision.
        }
        Shutdown(exitCode);
    }

    private void WriteNoticeDiagnostic(UI.NoticeDiagnostic sample)
    {
        try
        {
            var path = System.IO.Path.Combine(_runtime.Paths.Root, "notice-diagnostics.jsonl");
            System.IO.Directory.CreateDirectory(_runtime.Paths.Root);
            // Opt-in bounded local HWND/boolean samples only; never window titles or input contents.
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 1024 * 1024)
                System.IO.File.Move(path, path + ".1", true);
            System.IO.File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(sample) + Environment.NewLine);
        }
        catch (Exception ex) { _logger.TryWrite("Error", "NoticeDiagnosticFailed", ex); }
    }

    private void StartNoticePreview()
    {
        _noticeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _runtime.NoticeEventName);
        _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _runtime.StopEventName);
        _notifications = new WpfNotificationService(() => Interlocked.Exchange(ref _previewNotice, null),
            request => DateTimeOffset.UtcNow < request.ValidUntilUtc, _logger, WriteNoticeDiagnostic);
        _testCommands = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _testCommands.Tick += async (_, _) =>
        {
            if (_stopEvent.WaitOne(0)) { await StopAndShutdownAsync(0); return; }
            if (!_noticeEvent.WaitOne(0)) return;
            var kind = (TimeGuard.Models.NotificationKind)(_previewIndex++ % 5);
            var now = DateTimeOffset.UtcNow;
            var minutes = kind == TimeGuard.Models.NotificationKind.QuotaTenMinutes ? 10 :
                kind == TimeGuard.Models.NotificationKind.GraceStarted ? 20 : 5;
            var grace = kind is TimeGuard.Models.NotificationKind.GraceStarted or TimeGuard.Models.NotificationKind.GraceFiveMinutes;
            _previewNotice = new("preview", "preview", "ScreenTime preview", kind, now, now.AddSeconds(15),
                TimeSpan.FromMinutes(minutes), grace ? "preview" : null, grace ? now.AddMinutes(minutes) : null);
        };
        _testCommands.Start();
    }

    private void OpenSettings()
    {
        if (_stopping) return;
        var prompt = new UI.PasswordPromptWindow(_db!);
        if (prompt.ShowDialog() != true || _stopping) return;
        new UI.SettingsWindow(_db!, _runtime).ShowDialog();
        if (!_stopping) _monitor?.ReloadConfig(_db!.LoadConfig());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _power?.Dispose();
        _stopping = true;
        _testCommands?.Stop();
        _notifications?.Dispose();
        // Shutdown can also originate from WPF/session exit. Enforcement never waits
        // on this dispatcher, so joining the worker here cannot create a UI deadlock.
        if (_monitor is not null)
        {
            try { _monitor.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception) { e.ApplicationExitCode = 1; }
        }
        _hotkey?.Dispose();
        _settingsEvent?.Dispose();
        _stopEvent?.Dispose();
        _noticeEvent?.Dispose();
        if (_ownsMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _logger.TryWrite("Information", "ApplicationStopped");
        base.OnExit(e);
    }
}
