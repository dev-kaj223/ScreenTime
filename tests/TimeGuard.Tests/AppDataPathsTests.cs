using TimeGuard.Helpers;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class AppDataPathsTests
{
    [Fact]
    public void TestProfile_RejectsInstalledTimeGuardPaths()
    {
        var legacy = RuntimeOptions.LegacyProduction();
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Test(legacy.Paths.Root));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.TestDatabase(legacy.Paths.DatabasePath));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Test(Path.Combine(legacy.Paths.Root, "nested")));
    }

    [Fact]
    public void DefaultLaunch_IsDevelopment_InBothBuildConfigurations()
    {
        var runtime = RuntimeOptions.Resolve([]);
        Assert.Equal(RuntimeProfile.Development, runtime.Profile);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTime-Dev", "screentime.db"), runtime.Paths.DatabasePath);
        Assert.False(runtime.AllowsStartup);
        Assert.NotEqual(RuntimeOptions.LegacyProduction().MutexName, runtime.MutexName);
    }

    [Fact]
    public void TestProfiles_IsolateAllPathsAndIdentities()
    {
        using var first = new TempProfile();
        using var second = new TempProfile();
        var runtime = RuntimeOptions.Resolve(["--test-profile", first.Runtime.Paths.Root]);
        Assert.Equal(first.Runtime.Paths.Root, runtime.Paths.Root);
        Assert.NotEqual(first.Runtime.MutexName, second.Runtime.MutexName);
        Assert.NotEqual(first.Runtime.SettingsEventName, second.Runtime.SettingsEventName);
        Assert.Equal(first.Runtime.MutexName,
            RuntimeOptions.Test(first.Runtime.Paths.Root + Path.DirectorySeparatorChar).MutexName);
        Assert.False(runtime.AllowsGlobalHotkey);
        Assert.False(runtime.AllowsStartup);
        foreach (var path in new[] { runtime.Paths.DatabasePath, runtime.Paths.LogPath,
                     runtime.Paths.RuntimeDirectory, runtime.Paths.OwnedProcessesPath })
            Assert.StartsWith(runtime.Paths.Root + Path.DirectorySeparatorChar, path);
        Assert.False(Directory.Exists(runtime.Paths.Root));
    }

    [Fact]
    public void SuppliedDatabase_StorageAndLogsUseOnlyItsDirectory()
    {
        using var profile = new TempProfile();
        var legacyRoot = RuntimeOptions.LegacyProduction().Paths.Root;
        var existed = Directory.Exists(legacyRoot);
        var runtime = RuntimeOptions.Resolve(["--test-db", profile.Runtime.Paths.DatabasePath]);
        var db = new DatabaseService($"Data Source={runtime.Paths.DatabasePath}");
        db.SetSetting("test-setting", "test-value");
        new JsonFileLogger(runtime).Write("Information", "StorageTest");
        Assert.True(File.Exists(runtime.Paths.DatabasePath));
        Assert.True(File.Exists(runtime.Paths.LogPath));
        Assert.Equal(profile.Runtime.Paths.Root, runtime.Paths.Root);
        Assert.Equal(existed, Directory.Exists(legacyRoot));
    }

    [Fact]
    public void DevelopmentStorage_DoesNotCreateLegacyDirectory()
    {
        using var profile = new TempProfile();
        var dev = RuntimeOptions.Development(profile.Runtime.Paths.Root);
        var legacy = RuntimeOptions.LegacyProduction(profile.Runtime.Paths.Root);
        _ = new DatabaseService(dev.Paths);
        Assert.True(File.Exists(dev.Paths.DatabasePath));
        Assert.False(Directory.Exists(legacy.Paths.Root));
    }

    [Fact]
    public void TestArguments_TakePriorityOverInheritedEnvironment()
    {
        using var profile = new TempProfile();
        Assert.Equal(profile.Runtime.Paths.Root,
            RuntimeOptions.Resolve(["--test-profile", profile.Runtime.Paths.Root], "ignored.db").Paths.Root);
        Assert.Equal(profile.Runtime.Paths.Root,
            RuntimeOptions.Resolve([], profile.Runtime.Paths.DatabasePath).Paths.Root);
    }

    [Fact]
    public void DevAndTestStartupOperations_AreNoOps()
    {
        using var profile = new TempProfile();
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
        var before = key?.GetValueNames().Order().Select(n => (n, key.GetValue(n))).ToArray();
        foreach (var runtime in new[] { profile.Runtime, RuntimeOptions.Development() })
        {
            StartupHelper.Register(runtime);
            StartupHelper.Unregister(runtime);
            Assert.False(StartupHelper.IsRegistered(runtime));
        }
        Assert.Equal(before, key?.GetValueNames().Order().Select(n => (n, key.GetValue(n))).ToArray());
    }
}
