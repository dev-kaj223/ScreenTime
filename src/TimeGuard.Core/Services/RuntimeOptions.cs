using System.Security.Cryptography;
using System.Text;

namespace TimeGuard.Services;

public enum RuntimeProfile { Development, Test, LegacyProduction, Production }

public sealed class RuntimeOptions
{
    public RuntimeProfile Profile { get; }
    public AppDataPaths Paths { get; }
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public bool AllowsStartup => Profile == RuntimeProfile.LegacyProduction;
    public bool AllowsGlobalHotkey => Profile != RuntimeProfile.Test;
    public string MutexName { get; }
    public string SettingsEventName => MutexName + "-Settings";
    public string StopEventName => MutexName + "-Stop";
    public string NoticeEventName => MutexName + "-NoticePreview";
    public string StatusEventName => MutexName + "-Status";
    public string ExitEventName => MutexName + "-ProtectedExit";

    private RuntimeOptions(RuntimeProfile profile, AppDataPaths paths)
    {
        var legacyRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeGuard"));
        if (profile != RuntimeProfile.LegacyProduction &&
            (paths.Root.Equals(legacyRoot, StringComparison.OrdinalIgnoreCase) ||
             paths.Root.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("ScreenTime profiles cannot use the installed TimeGuard directory.");
        if (profile == RuntimeProfile.Test)
        {
            foreach (var name in new[] { "ScreenTime", "ScreenTime-Dev" })
            {
                var reserved = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name));
                if (paths.Root.Equals(reserved, StringComparison.OrdinalIgnoreCase) ||
                    paths.Root.StartsWith(reserved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Test profiles cannot use an existing ScreenTime user profile.");
            }
        }
        Profile = profile;
        Paths = paths;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "\\" + Environment.UserName + "|" + paths.Root.ToUpperInvariant())));
        MutexName = profile == RuntimeProfile.LegacyProduction
            ? @"Global\TimeGuard-SingleInstance" : @"Local\ScreenTime-" + profile + "-" + identity;
    }

    public static RuntimeOptions Development(string? appDataRoot = null) => new(
        RuntimeProfile.Development, new AppDataPaths(Path.Combine(appDataRoot ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime-Dev")));

    public static RuntimeOptions Production(string? appDataRoot = null) => new(
        RuntimeProfile.Production, new AppDataPaths(Path.Combine(appDataRoot ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime")));

    // Legacy identity is retained for preservation comparisons, never selected by launch arguments.
    public static RuntimeOptions LegacyProduction(string? appDataRoot = null) => new(
        RuntimeProfile.LegacyProduction, new AppDataPaths(Path.Combine(appDataRoot ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeGuard"), "timeguard.db"));

    public static RuntimeOptions Test(string root) => new(RuntimeProfile.Test, new AppDataPaths(root));

    public static RuntimeOptions TestDatabase(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        return new(RuntimeProfile.Test, new AppDataPaths(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath)));
    }

    public static RuntimeOptions Resolve(string[] args, string? testDatabase = null,
        bool distributionBuild = false, string? appDataRoot = null)
    {
        string? Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            if (index < 0) return null;
            if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"Missing value for {flag}.");
            return args[index + 1];
        }
        var root = Value("--test-profile");
        if (root is not null) return Test(root);
        var db = Value("--test-db") ?? testDatabase;
        if (!string.IsNullOrWhiteSpace(db)) return TestDatabase(db);
        return Value("--profile") switch
        {
            null => distributionBuild ? Production(appDataRoot) : Development(appDataRoot),
            "development" => Development(appDataRoot),
            "production" when distributionBuild => Production(appDataRoot),
            _ => throw new ArgumentException("Unknown runtime profile.")
        };
    }
}
