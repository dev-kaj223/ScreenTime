namespace TimeGuard.Services;

public interface IAppLogger
{
    // Call sites supply fixed event names, never configuration, passwords or window content.
    void Write(string level, string eventName, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void TryWrite(this IAppLogger? logger, string level, string eventName, Exception? exception = null)
    {
        try { logger?.Write(level, eventName, exception); }
        catch { /* Diagnostics must never become an application failure. */ }
    }
}
