ScreenTime for Windows x64
=========================
This is an unsigned, self-contained portable package. Extract the entire ZIP to
a new user-writable folder and run ScreenTime.exe manually. Keep all delivered
files together. No administrator privileges or separately installed .NET runtime
are required. No installer, Run registration, scheduled task or updater is used.

A normal distribution launch creates its own %APPDATA%\ScreenTime profile.
Development builds use %APPDATA%\ScreenTime-Dev. Existing %APPDATA%\TimeGuard data
and startup registration are not read, imported or changed. Do not configure two
enforcers for the same app. First launch asks you to set a password, then opens
initial configuration. Left-click the tray icon for the read-only Dashboard.
Right-click for temporary status; click elsewhere or press Escape to dismiss it.
Settings and Exit require your password. Closing these views does not stop
monitoring. The production settings shortcut is Ctrl+Alt+S; development
uses Ctrl+Alt+Shift+S. The tray remains available if another app owns a shortcut.
Autostart is intentionally disabled in this distribution.

All policy, passwords, history and diagnostics remain local. No account or
network connection is required at runtime. Before replacing this folder, use
protected Exit and wait for the app to stop. Extract a future package into a
separate folder; do not overwrite a running application. Persisted grace retains
its original deadline across restart. Keep the data directory/backups when
removing the application folder; removal does not delete history. No automatic
schema downgrade or legacy migration is provided.

The package includes .NET/WindowsDesktop 8.0.31. .NET 8 support ends 2026-11-10:
https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
Re-audit/update the bundled runtime before a later public release; self-contained
applications do not acquire runtime fixes from a machine-wide .NET update.

manifest.json records source, SDK/runtime/dependency versions and file SHA-256
hashes. Verify the archive against its adjacent .sha256 file. LICENSE and
THIRD-PARTY-NOTICES.txt preserve the project/upstream and bundled dependencies.
Signing, public publication and real-user installation are separate owner actions.
