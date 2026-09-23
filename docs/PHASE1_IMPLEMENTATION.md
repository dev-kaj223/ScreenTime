# Phase 1 implementation review

Only development/test isolation, local diagnostics, monitor task supervision, and test-process ownership were implemented. The architecture document was read in full. Rule evaluation, storage schema, quotas, allowed windows, persistent blocking, breaks, popup presentation, and history behavior remain unchanged. Existing `TimeGuard.*` projects and namespaces remain.

## Runtime profiles

Both Debug and Release default to the development profile; `--profile development` also selects it explicitly.

- Data directory: `C:\Users\Ouroboros\AppData\Roaming\ScreenTime-Dev\`
- Database: `C:\Users\Ouroboros\AppData\Roaming\ScreenTime-Dev\screentime.db`
- Log: `C:\Users\Ouroboros\AppData\Roaming\ScreenTime-Dev\logs\diagnostics.jsonl`
- Rotated log: the same log path with `.1` appended. Rotation occurs at 1 MiB, retaining one backup; individual exception fields are bounded.
- Runtime directory, when needed: `C:\Users\Ouroboros\AppData\Roaming\ScreenTime-Dev\runtime\`
- Development settings shortcut: **Ctrl+Alt+Shift+S**, distinct from the legacy default.

Tests allocate `%TEMP%\ScreenTime-tests\<unique GUID>\`, with database, SQLite sidecars, logs, and owned-process receipts inside that directory. Fixtures remove their directories after their owned processes exit. `--test-profile` selects a complete test profile. Compatibility `--test-db` and `TIMEGUARD_TEST_DB` resolve logs/runtime state alongside the supplied database, without creating a default directory. Test paths inside the installed TimeGuard directory are rejected.

Mutex names incorporate the profile, current user, and normalized directory identity. Tests register no global hotkey; profile-specific local events open the normal password-protected settings flow and request orderly shutdown. Development/test startup helper operations are no-ops, including Settings checkbox handlers. The explicit `legacy-production` profile retains legacy paths and startup behavior for deliberate legacy use; it was not launched during this task.

## Diagnostics and lifecycle

The lightweight JSON Lines logger records UTC timestamp, level, event name, run ID, profile, exception type/message/stack, and inner exception details. Call sites do not pass passwords, hashes/salts, configuration objects, window titles, or game content. Logging failures are contained and never fall back to writing into another profile.

The monitor retains its task, exposes `Completion`, `LastFault`, `StoppingToken`, and `StopAsync`, and prevents duplicate workers. Cancellation is normal; session closure happens after the loop finishes. Unexpected worker failures are logged, observable by callers, and cause the application to shut down with a nonzero exit code. Expected process-exit/access races are handled during enumeration. Dispatcher waits remain synchronous during ordinary enforcement but become cancellable during shutdown, including a modal callback. No enforcement policy was redesigned.

UI fixtures launch the matching build configuration through project dependencies. They no longer kill all TimeGuard processes or use Notepad. Popup tests launch the dedicated `ScreenTime.TestProcess` helper. Teardown and test-mode enforcement validate PID, creation time, and process name, retaining a handle across validation and termination; they never terminate process trees. Test-mode enforcement additionally requires the dedicated helper name and the fixture's ownership receipt.

## Existing test adaptations

- `MonitorServiceTests.cs` and `StorageServiceTests.cs`: existing assertions preserved; database and sidecar state moved into unique temporary directories with complete cleanup. The legacy migration test creates that directory before directly opening SQLite.
- `AppFixture.cs`: complete temporary profiles, owned process teardown, profile signals, no global environment mutation, and matching build artifacts.
- `PopupTests.cs`: replaced Notepad with the owned helper, preserving popup assertions and adding an actual helper-exit assertion.
- `SettingsWindowTests.cs`, `RuleEditWindowTests.cs`, and `DashboardWindowTests.cs`: replaced the production global shortcut with the fixture's settings signal; password and UI assertions remain.
- `Helpers/WindowHelpers.cs`: explicitly focuses only the located fixture window before test keyboard input, because the replacement signal does not carry hotkey foreground activation.
- `AssemblyInfo.cs`: updated the serial-test comment; desktop input still requires serial UI tests.

The original 37 unit and 18 UI tests remain. New coverage exercises profile paths/identities, startup no-ops, structured exception logs and rotation, logging failure, cancellation/stop/duplicate start, observable faults, session closure, same-name process ownership, creation-identity mismatches, application failure exit, and modal-dialog shutdown.

## Verification

- `dotnet build`: succeeded, **1 warning, 0 errors**. The warning is the existing CS0618 for `Model.MouseDown` in `DashboardWindow.xaml.cs`; no new compiler/analyzer warnings remain.
- `dotnet test`: **77 passed, 0 failed, 0 skipped** — 53 unit tests and 24 UI tests. All original 55 tests remain; 22 tests were added.
- `git diff --check`: passed.
- Installed `%AppData%\TimeGuard\` was not intentionally modified. Read-only before/after checks confirmed identical file lists and SHA-256 hashes for its three files.
- All HKCU Run names/values matched the before snapshot. No dev/test startup entry was created; the existing TimeGuard entry was unchanged.
- `git diff --stat`: 17 tracked files, 445 insertions and 299 deletions. Including 14 new files (tests, helper, runtime services, and this report): 31 files, 1145 insertions and 299 deletions.
- `git status`: branch `screentime-dev`, 17 modified tracked files plus 14 new files; all unstaged/uncommitted. No commit or push was performed.

## Files changed and created

Modified existing files:

- `TimeGuard.sln`
- `src/TimeGuard.App/Helpers/StartupHelper.cs`
- `src/TimeGuard.App/UI/App.xaml.cs`
- `src/TimeGuard.App/UI/SettingsWindow.xaml.cs`
- `src/TimeGuard.Core/Services/DatabaseService.cs`
- `src/TimeGuard.Core/Services/MonitorService.cs`
- `tests/TimeGuard.Tests/MonitorServiceTests.cs`
- `tests/TimeGuard.Tests/StorageServiceTests.cs`
- `tests/TimeGuard.Tests/TimeGuard.Tests.csproj`
- `tests/TimeGuard.UITests/AppFixture.cs`
- `tests/TimeGuard.UITests/AssemblyInfo.cs`
- `tests/TimeGuard.UITests/DashboardWindowTests.cs`
- `tests/TimeGuard.UITests/Helpers/WindowHelpers.cs`
- `tests/TimeGuard.UITests/PopupTests.cs`
- `tests/TimeGuard.UITests/RuleEditWindowTests.cs`
- `tests/TimeGuard.UITests/SettingsWindowTests.cs`
- `tests/TimeGuard.UITests/TimeGuard.UITests.csproj`

Created files:

- `docs/PHASE1_IMPLEMENTATION.md`
- `src/TimeGuard.App/Services/JsonFileLogger.cs`
- `src/TimeGuard.Core/Properties/AssemblyInfo.cs`
- `src/TimeGuard.Core/Services/AppDataPaths.cs`
- `src/TimeGuard.Core/Services/IAppLogger.cs`
- `src/TimeGuard.Core/Services/OwnedProcessIdentity.cs`
- `src/TimeGuard.Core/Services/RuntimeOptions.cs`
- `tests/ScreenTime.TestProcess/Program.cs`
- `tests/ScreenTime.TestProcess/ScreenTime.TestProcess.csproj`
- `tests/TimeGuard.Tests/AppDataPathsTests.cs`
- `tests/TimeGuard.Tests/DiagnosticsTests.cs`
- `tests/TimeGuard.Tests/MonitorLifecycleTests.cs`
- `tests/TimeGuard.Tests/TempProfile.cs`
- `tests/TimeGuard.UITests/TestIsolationTests.cs`
