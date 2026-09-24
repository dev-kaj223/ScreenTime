# Phase 2 implementation review

Phase 2 only: explicit policy facts/decisions, serialized coordination, and Core process-instance enforcement. The complete architecture plan and Phase 1 implementation were read before editing. No commit or push was made.

## Implemented state and boundaries

`PolicySnapshot` is an immutable record of copied scalar facts: canonical app key, display label, enabled state, explicit accounting date/local time, that weekday's allowance and legacy allowed window, usage minutes, warning receipt, and running presence. Capture never calls `DailyLog.GetOrCreate`. `RulesEngine.Evaluate` accepts only this snapshot and returns an immutable `PolicyDecision` without storage, process, clock, or WPF calls.

The implemented states are `Available`, `TemporaryScheduleRestriction`, `DailyQuotaBlocked`, and `Warning`. Decisions distinguish launch permission, continuation permission, termination intent, and the existing five-minute warning intent. Schedule and quota reasons are retained simultaneously; schedule denial is the primary display reason during overlap. Disabled rules impose no restriction. Stopped applications still receive launch/status decisions without a termination command.

`UsageEntry.Blocked` remains readable and round-trippable for compatibility, but neither evaluation nor accounting consults it and the coordinator never writes a new block flag. Outside-window denial therefore does not become a daily ban. Inside the window, remaining quota permits use even if a legacy row contains `Blocked=true`; exhausted usage independently continues to deny use. An authorized allowance increase can permit use again.

`MonitorService` retains the supervised task, observed faults, idempotent start/stop, cancellation, awaited shutdown, and cleanup-failure diagnostics. It owns mutable usage, sessions, and enforcement state. Reload publishes a private copy of weekday rules, consumed at the next serialized tick (normally within five seconds plus processing). Reload does not toggle persistent blocks. Readers receive read-only decision/result collections. There is no second enforcement timer or UI-owned permission state.

Only configured enabled applications are observed/accounted. The MVP supports one configured application without requiring passive discovery; existing multiple configured rows retain independent app-keyed evaluation without aggregate coordination. Multiple instances count once per app per tick. Unrelated processes produce no new usage or passive history. Overall caps and enforced breaks are inactive; their existing settings, editor fields, storage, and historical APIs remain readable.

## Process enforcement

- `ProcessInstance`: normalized name without `.exe`, PID, creation UTC ticks, and Windows session ID.
- `IProcessMonitor` / `WindowsProcessMonitor`: enumerate configured candidate names at the existing five-second cadence, capture concrete identities in the current session, tolerate exit/access races, and dispose every process wrapper. A window title is neither needed nor collected.
- `IProcessTerminator` / `WindowsProcessTerminator`: open only the requested PID, retain its handle, recheck creation time, app key, PID and session, and query token ownership to refuse another user's process. Kill only that instance and confirm exit with a bounded asynchronous wait. No name-wide kill, process-tree termination, elevation, memory inspection, hooks, or game integration.
- Results distinguish terminated, already exited, mismatched identity, access denied, timeout, failure, and no request. The Core `EnforcementService` validates the decision/target pairing, delegates termination, returns the outcome, and records failures through the Phase 1 logger. Failed enforcement leaves policy denied; later ordinary observations can retry.
- `TestProcessScope` preserves Phase 1's dedicated-helper ownership receipts for both discovery and termination, including rechecking scope immediately before enforcement. Another fixture's same-name helper is excluded.

The coordinator performs enforcement for all current targets before notification callbacks. `App.xaml.cs` contains no kill code and only queues the existing popups asynchronously. Callback/display errors are logged and do not change permission or fault monitoring. A slow WPF dispatcher does not hold up process termination. Actual process existence on the next observation, rather than a popup or kill request, controls session closure.

Application matching remains configured process-name matching. Production observations of the configured name are policy candidates; exact instance revalidation prevents collateral termination of replacements or other instances outside the supplied target set. The test/manual checks additionally constrain targets to explicitly owned helper identities. An executable-path selector and broader application identity UX are not introduced.

## Compatibility and corrected defects

Preserved: existing inclusive same-day allowed windows, `0 = unlimited`, immediate quota enforcement (no grace), five-minute warning requests, minute-based SQLite usage, approximately five-second fixed sampling, existing popup visuals/buttons/lifetimes/text, settings password protection, profile isolation, startup behavior, diagnostics, and legacy history access. Popup wording still has the legacy midnight/reset limitation; notification redesign is deferred.

Corrected: persistent schedule bans, evaluator mutation of missing usage rows, stale uniform fields controlling weekday reload/save, name-wide WPF termination, synchronous WPF enforcement dependency, duplicate initial block/relaunch popups, passive unrelated-app charging, and active overall-cap/break policy in the MVP path. Denied observations are evaluated before charging usage. Five-second tick accounting remains approximate; measured elapsed accounting, sleep/gap classification, second-based storage, and midnight splitting are Phase 3 work.

`DatabaseService.SaveRule` uses supplied weekday rows as authoritative. Uniform legacy fields remain a fallback only when no weekday rows exist. Callers editing an existing weekly rule must update its weekday rows. Settings now reports duplicate canonical process keys without crashing.

## Database impact

The existing unversioned schema upgrades transactionally to `PRAGMA user_version=1`. Existing profile databases receive a consistent SQLite backup at `<database>.pre-phase2.bak` before migration. New databases need no backup. Reopening version 1 performs no migration; unknown newer versions are rejected.

The upgrade preserves all existing tables/columns and normalizes process keys in rules, usage, and history. Unique indexes enforce one canonical rule per application and one usage row per application/date. Conflicting existing canonical keys fail and roll back the upgrade rather than merging differing policies or losing usage. The backup remains available for review/recovery. No new usage units, blocked-period tables, grace tables, notification receipts, break state, or overall-cap ledger were added. Legacy `Blocked` data is retained without granting it policy authority.

## Deliberate test adaptations

- `RulesEngineTests.cs`: retained the original quota, warning, window, weekday, absence, relaunch, overall-cap, and break scenarios using explicit snapshots/decisions. Replaced expected legacy actions with permission/reason assertions. Persistent-block shortcuts and overall/break enforcement expectations now assert the corrected MVP behavior. Added immutable-input, disabled/unlimited, endpoint, and simultaneous-reason checks.
- `MonitorServiceTests.cs`: retained raised/unchanged/removed allowance and warning-reset intent, testing serialized reevaluation instead of persistent block toggles. Assert today's weekday overrides stale uniform fields and queued inputs cannot be changed by the caller. Existing aggregate-cap cases now assert inactivity.
- `MonitorLifecycleTests.cs`: inject concrete fake process instances and a fake terminator. Cleanup tests explicitly configure the helper because passive tracking is removed. A worker fault is injected through observation, not a UI callback, since UI exceptions are now intentionally contained. Original lifecycle/fault/cleanup assertions remain.
- `StorageServiceTests.cs`: edit weekday rows when updating an existing rule; preserve all storage coverage.
- `AppFixture.cs`: optional headless owned helper launch for title-independent discovery testing.
- `PopupTests.cs`: expect one initial popup instead of the defective duplicate; preserve title/OK checks and add quota relaunch denial after popup closure.
- `TestIsolationTests.cs`: explicitly configure a helper before injecting a session-table fault; preserve all ownership/startup/shutdown checks and add real headless discovery and exact-instance termination.
- `RuleEditWindowTests.cs`: use distinct names for independently successful rule-add cases and direct UI Automation field values/button invocation to avoid redirected keyboard input; preserve legacy editor validations and add duplicate-key error handling coverage.
- `ScreenTime.TestProcess/Program.cs`: optional `--headless` mode; the visible helper remains the default.

New tests cover deterministic state/usage boundaries, PID reuse/name/session mismatch, vanished/inaccessible processes, another-user rejection, UI-independent enforcement, failure visibility/logging, selected-app accounting, canonical migration/rollback/backup/versioning, and weekday persistence authority. Forced termination uses only the owned `ScreenTime.TestProcess` helper; no Apex or ranked gameplay tests were run.

## Manual validation performed

A command-driven real-process smoke run used `RuntimeProfile.Development` with an explicitly overridden, fresh workspace profile:

`tests/TimeGuard.Tests/bin/Phase2Smoke/runs/01d14eaca585432592c3780c17c53542/ScreenTime-Dev`

This was a development-profile Core/Windows-adapter run, not a claim of manually clicking the default installed WPF app. The ordinary five-second monitor ran against real headless helper processes, with an explicit owned target scope and the Phase 1 JSON logger. The temporary harness/artifacts are under ignored build output.

Verified outcomes:

1. A helper launched outside a short existing allowed window was terminated despite a deliberately throwing notification subscriber.
2. Usage stayed at its seeded 0.85 minutes and `Blocked` stayed false during denial.
3. After the real wall clock crossed into the window, a new helper survived and used remaining quota.
4. Saving/reloading today's one-minute weekday allowance took effect despite a deliberately stale legacy limit of 999.
5. Exhaustion terminated the helper inside the permissive window and denied a subsequent relaunch.
6. A separate same-name owned helper outside the selected target scope survived all enforcement.
7. `NotificationFailed` appeared in structured diagnostics and the supervised monitor shut down cleanly.

The WPF UI suite separately verifies actual popup closure and subsequent quota relaunch denial, headless helper termination, same-name ownership isolation, modal-settings shutdown, and monitor-fault reporting. Synthetic access-denied and PID-reuse cases use handle fakes, never a protected real application.

## Deferred functionality

No Phase 3 explicit downtime/blocked periods, cross-midnight scheduling, overnight/daytime downtime examples, second-based accounting schema, or sleep/midnight redesign. No Phase 4 grace states, episodes, tables, deadlines, exit observers, or Finish Current Session behavior. No Phase 5 notification redesign, ten-minute warnings, nonactivation/pass-through changes, or tray UI. No rename, TimeGuard import, scheduler recovery, multi-app cap redesign, enforced-break redesign, or broad history/dashboard work.

## Files changed and created

Modified:

- `src/TimeGuard.App/UI/App.xaml.cs`
- `src/TimeGuard.App/UI/SettingsWindow.xaml.cs`
- `src/TimeGuard.Core/Models/DailyLog.cs`
- `src/TimeGuard.Core/Services/DatabaseMigrator.cs`
- `src/TimeGuard.Core/Services/DatabaseService.cs`
- `src/TimeGuard.Core/Services/MonitorService.cs`
- `src/TimeGuard.Core/Services/RulesEngine.cs`
- `tests/ScreenTime.TestProcess/Program.cs`
- `tests/TimeGuard.Tests/MonitorLifecycleTests.cs`
- `tests/TimeGuard.Tests/MonitorServiceTests.cs`
- `tests/TimeGuard.Tests/RulesEngineTests.cs`
- `tests/TimeGuard.Tests/StorageServiceTests.cs`
- `tests/TimeGuard.UITests/AppFixture.cs`
- `tests/TimeGuard.UITests/PopupTests.cs`
- `tests/TimeGuard.UITests/RuleEditWindowTests.cs`
- `tests/TimeGuard.UITests/TestIsolationTests.cs`

Created:

- `src/TimeGuard.Core/Models/PolicyDecision.cs`
- `src/TimeGuard.Core/Models/PolicySnapshot.cs`
- `src/TimeGuard.Core/Models/ProcessInstance.cs`
- `src/TimeGuard.Core/Services/EnforcementService.cs`
- `src/TimeGuard.Core/Services/IProcessMonitor.cs`
- `src/TimeGuard.Core/Services/IProcessTerminator.cs`
- `src/TimeGuard.Core/Services/IStateStore.cs`
- `src/TimeGuard.Core/Services/TestProcessScope.cs`
- `src/TimeGuard.Core/Services/WindowsProcessMonitor.cs`
- `src/TimeGuard.Core/Services/WindowsProcessTerminator.cs`
- `tests/TimeGuard.Tests/EnforcementServiceTests.cs`
- `tests/TimeGuard.Tests/Phase2MigrationTests.cs`
- `tests/TimeGuard.Tests/PolicyStateTests.cs`
- `tests/TimeGuard.Tests/PolicyTestDoubles.cs`
- `docs/PHASE2_IMPLEMENTATION.md`

## Final verification

- Final nonincremental `dotnet build --no-incremental`: succeeded, **1 warning, 0 errors**. The only warning is the pre-existing CS0618 for `Model.MouseDown` in `DashboardWindow.xaml.cs`; no new warning remains. Plain `dotnet build` also succeeded earlier.
- Final `dotnet test`: **111 passed, 0 failed, 0 skipped** — **84 Core/unit tests and 27 UI tests**. This is 34 more passing cases than the approved Phase 1 baseline of 77.
- `git diff --check`: passed.
- Read-only before/after verification found the installed `%AppData%\TimeGuard` file list and all three SHA-256 hashes unchanged. No installed TimeGuard database/log file was modified by this work.
- Every HKCU Run name/value matched the pre-task snapshot, including `TimeGuard`. No startup entry changed.
- `git status`: 16 modified tracked files and 15 new source/report files on `screentime-dev`, all unstaged. No commit or push.

`git diff --stat` (tracked files; Git omits the 15 untracked new files from this command):

```text
 src/TimeGuard.App/UI/App.xaml.cs                |  70 ++---
 src/TimeGuard.App/UI/SettingsWindow.xaml.cs     |  22 +-
 src/TimeGuard.Core/Models/DailyLog.cs           |   4 +-
 src/TimeGuard.Core/Services/DatabaseMigrator.cs |  56 +++-
 src/TimeGuard.Core/Services/DatabaseService.cs  |  25 +-
 src/TimeGuard.Core/Services/MonitorService.cs   | 305 +++++++++-------------
 src/TimeGuard.Core/Services/RulesEngine.cs      | 160 ++----------
 tests/ScreenTime.TestProcess/Program.cs         |   7 +-
 tests/TimeGuard.Tests/MonitorLifecycleTests.cs  |  31 ++-
 tests/TimeGuard.Tests/MonitorServiceTests.cs    | 215 ++++------------
 tests/TimeGuard.Tests/RulesEngineTests.cs       | 327 ++++++------------------
 tests/TimeGuard.Tests/StorageServiceTests.cs    |   2 +-
 tests/TimeGuard.UITests/AppFixture.cs           |   8 +-
 tests/TimeGuard.UITests/PopupTests.cs           |  20 +-
 tests/TimeGuard.UITests/RuleEditWindowTests.cs  |  39 ++-
 tests/TimeGuard.UITests/TestIsolationTests.cs   |  36 ++-
 16 files changed, 467 insertions(+), 860 deletions(-)
```

Including the 15 new files listed above: 31 files, 1192 insertions and 860 deletions (new-file line counts included).
