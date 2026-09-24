using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.UIA3;
using Microsoft.Data.Sqlite;
using TimeGuard.Services;

namespace TimeGuard.UITests;

/// <summary>Each fixture owns a complete temporary profile and only processes it launches.</summary>
public class AppFixture : IDisposable
{
    public static string TestPassword =>
        Environment.GetEnvironmentVariable("TIMEGUARD_TEST_PASSWORD") ?? "test123";

    public RuntimeOptions Runtime { get; } = RuntimeOptions.Test(
        Path.Combine(Path.GetTempPath(), "ScreenTime-tests", Guid.NewGuid().ToString("N")));
    protected string DbPath => Runtime.Paths.DatabasePath;
    private Application? _app;
    private UIA3Automation? _automation;
    private OwnedProcessIdentity? _appIdentity;
    private readonly List<OwnedProcessIdentity> _helpers = [];
    private bool _disposed;
    public Application App => _app ?? throw new InvalidOperationException("App not launched.");
    public UIA3Automation Automation => _automation ?? throw new InvalidOperationException("App not launched.");

    public AppFixture()
    {
        try { SeedDatabase(); Launch(); }
        catch { Dispose(); throw; }
    }

    protected virtual void SeedDatabase() { }
    protected virtual bool PreviewNotices => false;

    protected void SaveConfigToDb(string hash, string salt)
    {
        var db = OpenDb();
        var config = db.LoadConfig();
        config.PasswordHash = hash;
        config.PasswordSalt = salt;
        db.SaveConfig(config);
    }

    protected DatabaseService OpenDb() => new(Runtime.Paths);
    public DatabaseService OpenDatabase() => OpenDb();

    protected static (string hash, string salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
            saltBytes, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    private void Launch()
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "App", "TimeGuard.exe"))
        { UseShellExecute = false };
        info.ArgumentList.Add("--test-profile");
        info.ArgumentList.Add(Runtime.Paths.Root);
        info.ArgumentList.Add("--notice-diagnostics");
        if (PreviewNotices) info.ArgumentList.Add("--preview-notices");
        info.Environment.Remove("TIMEGUARD_TEST_DB");
        _automation = new UIA3Automation();
        _app = Application.Launch(info);
        using var process = Process.GetProcessById(_app.ProcessId);
        _appIdentity = OwnedProcessIdentity.Capture(process);
        Thread.Sleep(2500);
        if (process.HasExited) throw new InvalidOperationException("Isolated app exited during startup.");
    }

    public void RequestSettings()
    {
        using var signal = EventWaitHandle.OpenExisting(Runtime.SettingsEventName);
        signal.Set();
    }

    public OwnedProcessIdentity LaunchHelper(bool headless = false, bool inputProbe = false)
    {
        var info = new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "Helper", "ScreenTime.TestProcess.exe"))
            { UseShellExecute = false };
        if (headless) info.ArgumentList.Add("--headless");
        if (inputProbe)
        {
            info.ArgumentList.Add("--input-probe");
            info.ArgumentList.Add(Path.Combine(Runtime.Paths.Root, "helper-input.txt"));
        }
        using var process = Process.Start(info)!;
        var identity = OwnedProcessIdentity.Capture(process);
        _helpers.Add(identity);
        Directory.CreateDirectory(Runtime.Paths.RuntimeDirectory);
        var staging = Runtime.Paths.OwnedProcessesPath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(_helpers));
        File.Move(staging, Runtime.Paths.OwnedProcessesPath, overwrite: true);
        return identity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_appIdentity is not null)
            {
                try
                {
                    using var stop = EventWaitHandle.OpenExisting(Runtime.StopEventName);
                    stop.Set();
                    using var process = Process.GetProcessById(_appIdentity.Id);
                    if (_appIdentity.Matches(process)) process.WaitForExit(5000);
                }
                catch (WaitHandleCannotBeOpenedException) { /* First-run dialog has no monitor yet. */ }
                catch (ArgumentException) { /* Already exited. */ }
                _appIdentity.Terminate();
            }
        }
        finally
        {
            foreach (var helper in _helpers) helper.Terminate();
            _app?.Dispose();
            _automation?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Runtime.Paths.Root)) Directory.Delete(Runtime.Paths.Root, recursive: true);
        }
    }
}

public class SeededAppFixture : AppFixture
{
    protected override void SeedDatabase()
    {
        var (hash, salt) = HashPassword(TestPassword);
        SaveConfigToDb(hash, salt);
    }
}
