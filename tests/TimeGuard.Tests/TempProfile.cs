using Microsoft.Data.Sqlite;
using TimeGuard.Services;

namespace TimeGuard.Tests;

public sealed class TempProfile : IDisposable
{
    public RuntimeOptions Runtime { get; } = RuntimeOptions.Test(
        Path.Combine(Path.GetTempPath(), "ScreenTime-tests", Guid.NewGuid().ToString("N")));
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Runtime.Paths.Root)) Directory.Delete(Runtime.Paths.Root, recursive: true);
    }
}
