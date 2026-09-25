using System.IO;
using System.Text.Json;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Separate presentation file: preferences can never hold the policy SQLite writer lock.</summary>
internal sealed class NotificationPreferenceStore(AppDataPaths paths, IAppLogger? logger = null)
{
    internal string Path => System.IO.Path.Combine(paths.Root, "notification-preferences.json");
    public NotificationPreferences Load()
    {
        try { return File.Exists(Path) ? NotificationPreferences.Parse(File.ReadAllText(Path)) : NotificationPreferences.Standard; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.TryWrite("Error", "NotificationPreferencesUnreadable", ex);
            return NotificationPreferences.Standard;
        }
    }

    public void Save(NotificationPreferences preferences)
    {
        preferences.Validate();
        Directory.CreateDirectory(paths.Root);
        var staging = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences);
                stream.Flush(true);
            }
            File.Move(staging, Path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
