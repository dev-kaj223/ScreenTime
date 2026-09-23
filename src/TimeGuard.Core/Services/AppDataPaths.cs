namespace TimeGuard.Services;

/// <summary>Pure path resolution: constructing paths never creates a directory.</summary>
public sealed class AppDataPaths
{
    public string Root { get; }
    public string DatabasePath { get; }
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string LogPath => Path.Combine(LogsDirectory, "diagnostics.jsonl");
    public string RuntimeDirectory => Path.Combine(Root, "runtime");
    public string OwnedProcessesPath => Path.Combine(RuntimeDirectory, "owned-processes.json");

    public AppDataPaths(string root, string databaseName = "screentime.db")
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        DatabasePath = Path.Combine(Root, databaseName);
    }
}
