using TimeGuard.Helpers;

namespace TimeGuard.Services;

/// <summary>One command boundary for both Settings and Exit. Each invocation authenticates anew.</summary>
internal sealed class SettingsAccessService(Func<(string Hash, string Salt)> credentials,
    Func<string?> prompt, Action settings, Func<Task> exit, Func<bool> stopping, bool initialConfigurationAuthorized = false)
{
    private bool _busy;
    private bool _initialConfigurationAuthorized = initialConfigurationAuthorized;
    internal void OpenInitialConfiguration()
    {
        if (!_initialConfigurationAuthorized || _busy || stopping()) return;
        _initialConfigurationAuthorized = false;
        _busy = true;
        try { settings(); }
        finally { _busy = false; }
    }
    public void OpenSettings() => ExecuteSettings();
    private void ExecuteSettings()
    {
        if (_busy || stopping()) return;
        _busy = true;
        try { if (Authenticate() && !stopping()) settings(); }
        finally { _busy = false; }
    }

    public async Task ExitAsync()
    {
        if (_busy || stopping()) return;
        _busy = true;
        try { if (Authenticate() && !stopping()) await exit(); }
        finally { _busy = false; }
    }

    private bool Authenticate()
    {
        var password = prompt();
        if (password is null) return false;
        var (hash, salt) = credentials();
        try { return !string.IsNullOrEmpty(hash) && PasswordHelper.Verify(password, hash, salt); }
        catch (FormatException) { return false; }
    }
}
