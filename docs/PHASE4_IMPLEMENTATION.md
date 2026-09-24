# Phase 4 implementation review

Phase 4 only: persistent Finish Current Session grace. Phases 1–3 were read before editing. Production/default grace is exactly 20 minutes. No commit or push.

## Durable model and migration

Schema version is **3** (`PRAGMA user_version`). Version 2 upgrades within one SQLite transaction, after a consistent SQLite backup at `<database>.pre-phase4.bak`. Earlier upgrades retain their Phase 2/3 backups and also take the Phase 4 backup. Fresh profiles require no backup; reopening version 3 is idempotent and unknown future versions are rejected. The installed TimeGuard path remains rejected before opening. Service and migration connections explicitly select `synchronous=FULL`; foreign keys remain enabled and WAL is preserved.

`GraceEpisodes` contains:

- `Id TEXT PRIMARY KEY NOT NULL`, a generated episode identifier.
- `AppKey TEXT NOT NULL`, checked as a nonempty canonical lowercase process name without `.exe`.
- `QuotaDate TEXT NOT NULL`, the original quota day's ISO local date.
- `StartedAtUtcTicks INTEGER NOT NULL` and `ExpiresAtUtcTicks INTEGER NOT NULL`, UTC ticks with a strictly positive duration.
- `Phase INTEGER NOT NULL`: 0 Active, 1 CompletedByExit, 2 Expired.
- `EndedAtUtcTicks INTEGER NULL`: null while active; a valid terminal time otherwise.
- Unique `(AppKey, QuotaDate)` prevents a second episode; a partial unique index on AppKey where Phase=0 prevents simultaneous active episodes for one app. Unique `(Id, AppKey)` supports the composite foreign key.

`GraceProcesses` contains `EpisodeId`, `AppKey`, `ProcessId`, `StartTimeUtcTicks`, and `SessionId`. Its primary key includes all five columns. `(EpisodeId, AppKey)` references the episode with foreign keys enforced. PID/start/session integer checks reject invalid identities. The captured list and deadline are immutable through the storage API. There is deliberately no cascading relationship to AppRules: deleting/recreating a rule cannot erase consumed grace history. Terminal episode and process rows remain durable.

`DailyUsage.GraceSeconds` is a nonnegative integer, constrained not to exceed ObservedSeconds. Existing rows start at zero. ObservedSeconds includes quota, grace, and observed denied runtime. GraceSeconds is a **subset**, never added to ObservedSeconds as another total. Legacy UsageMins continues to mirror QuotaSeconds/60 for compatibility.

An injected migration collision exposed a multi-statement command failing to surface a later DDL preparation error. Phase 4 now executes its individual DDL statements separately within the encompassing transaction. The regression verifies that a failed table creation rolls back both the added usage column and version, with the backup intact.

## Grant criteria and crossing calculation

The coordinator must have a previous permitted observation and a current observation of the same exact ProcessInstance. The enabled app must have positive remaining quota, then consume it in a continuous, permitted measured segment. There must be no consumed episode for that app/date and no other active episode for the app. Only previously permitted identities present in both observations are captured. An additional process first discovered at the crossing is denied conservatively; it is not inferred to have been present earlier.

UsageAccounting carries fractional monotonic ticks independently by app, local date, and classification. It splits intervals at local midnight, downtime boundaries, and known grace expiry. Remaining quota is `(limitSeconds - persistedQuotaSeconds) * ticksPerSecond - carriedQuotaTicks`. When the segment reaches that amount, its monotonic position maps proportionally onto the UTC segment. With unchanged wall time, two remaining seconds in a five-second observation produce a crossing exactly two seconds after the prior observation. Decimal tick arithmetic avoids floating-point boundary drift.

The crossing reports its app, quota date, exact UTC instant, and eligible identities to the coordinator. The deadline is **crossing + 20 minutes**, not observation + 20 minutes. The remainder of that segment becomes grace runtime through the deadline, including any subsequent midnight/downtime segments. A quota crossing exactly at the start of downtime does not grant a new exception; downtime alone never grants grace.

First discovery, replacement without continuity, an already-exhausted startup, or restart with exhausted quota and no episode cannot grant grace. The first observation and uncertain gaps continue to charge zero. Authorized configuration reload rebases accounting. Selected allowance-decrease behavior is conservative: a decrease that makes existing usage exhausted blocks immediately without minting grace. Raising the allowance can permit later normal use, but the durable app/date uniqueness prevents another grace after one was consumed.

## Atomic transition and failures

`IStateStore.CommitObservation` / `DatabaseService.CommitObservation` commit all affected usage dates, the episode, its fixed deadline, and all captures in one logical transaction. This includes the crossing interval's grace remainder. No continuation decision is published before commit, and notification success is irrelevant. The insert uses app/date conflict handling; a retry returns the stored episode, deadline, and capture set. A terminal episode cannot become active again.

The single serialized coordinator remains the usage writer. Retrying the same checkpoint does not add usage again. Fault injection verifies rollback when capture insertion fails and when usage validation fails after inserting the episode. Persistent database failure faults the existing supervised worker; the implementation does not grant uncommitted grace or invent a replacement deadline on restart. It does not introduce a separate recovery worker.

## State machine and exact-instance enforcement

Active transitions to CompletedByExit when all captures are confirmed gone before the deadline, or to Expired at the deadline. Expiry takes precedence when an observation arrives at/after the deadline. Exit time is the confirmation observation; expiry time is the stored deadline. Individual exits do not add replacements to the capture set, and the remaining captured instances may finish. Voluntary exit and crash are identical at this layer.

Pure Core `GracePolicy` applies the instance-specific exception. App-level grace status has MayLaunch=false and never represents permission for an arbitrary same-name process. Only `Apply(..., capturedInstance)` grants continuation. New PID, reused PID with different creation time, different session, and unrelated name do not inherit the exception. Authorized disabled rules retain their existing unrestricted precedence; consumed history survives configuration changes.

At expiry the coordinator persists Expired **before** calling EnforcementService. Existing WindowsProcessTerminator still opens the specific PID, retains the handle, revalidates app/PID/creation/session and current-user token ownership, and confirms termination. It never suspends, reprioritizes, injects, or kills a process tree. Outcomes remain exposed by EnforcementResults and failure diagnostics; a failed kill stays expired and is retried on later observations.

Windows process observation now distinguishes confirmed exit from an inaccessible process. A missing snapshot entry alone cannot prematurely complete grace. At expiry, a capture not confirmed exited is still sent for identity/owner revalidation even if enumeration could not read it. Historical expired survivors remain blocked as those exact instances, even when a later day's quota would permit a new launch.

The existing serialized worker includes active grace deadlines in its next-wake calculation alongside discovery, midnight, downtime, and fractional quota exhaustion. Ordinary discovery remains approximately five seconds; a known expiry does not deliberately wait another discovery period. OS scheduling, storage and termination confirmation still introduce execution latency. Exit completion happens at the first confirming observation, normally within the discovery interval; no additional timer/thread or exit watcher is introduced.

## Restart, midnight, downtime and sleep

Construction loads persisted episodes before the first policy evaluation. On the first tick, current exact identities are reconciled. A live capture before expiry gets only its original remaining time; missing captures complete the episode, replacements are denied, and an overdue episode is persisted expired and enforced immediately. Runtime during a ScreenTime outage is not reconstructed. An already-expired capture is retried after restart if still alive.

Monday 23:55 exhaustion stores Tuesday 00:15 expiry. Tuesday 00:00 creates a new daily bucket, and configured downtime denies new launches. The original capture retains Monday's grace until 00:15. Tuesday observed/grace runtime is recorded on Tuesday while Tuesday quota stays zero. Exit at 00:05 completes grace with downtime still active. Expiry terminates the capture and leaves downtime active. Availability returns at 08:00, or at 17:00 when adjacent daytime downtime exists. Without downtime, an expired carried survivor still must stop; a genuinely new instance may use the new day's normal allowance.

Suspend/resume and time/zone rebase signals retain Phase 3 behavior. Sleep adds no observed/quota/grace usage. Grace's UTC deadline does not pause. Resume after expiry persists Expired and enforces immediately. Gaps over 30 seconds and large wall/monotonic discrepancies rebase rather than invent usage. Fractions below one second remain memory-only, as in Phase 3, and may be lost on restart.

## Notifications and deferred scope

No new grace-start popup was added. Existing legacy five-minute warnings and hard-block popup presentation remain. WPF owns no grace deadline, permission or enforcement. `GraceEpisodes`, the decision's `Grace` facts, state, and enforcement results expose data for Phase 5. Structured diagnostics include GraceActive, GraceCompletedByExit and GraceExpired after commit.

No ten-minute warning, passive/nonactivating notice redesign, overlay, tray/status UI, ranked detection, Apex inspection, hooks, injection, anti-cheat integration, Task Scheduler recovery, TimeGuard import, aggregate-cap grace or break redesign. **Phase 4 is not yet intended for real Apex use**; the existing warning UI awaits Phase 5.

## Tests and validation

Deterministic coverage includes exact/fractional crossing, grace remainder, captured multiple instances, first-observed exhaustion, restart without episode, replacements and PID reuse, exit/crash and partial exit, exact expiry, failed enforcement/retry, original remaining deadline after restart, expired-before-kill ordering, inaccessible captures, disabled policy precedence, allowance changes, rule recreation, midnight/downtime overlap and later availability, short sleep and overdue resume, atomic rollback/retry, v2 backup/migration/constraints and idempotence. Phase 1–3 tests remain; migration fixtures remove Phase 4 additions when constructing older schemas. The Phase 3 real-helper test uses a two-second test-only grace while retaining its final quota/termination assertions.

Real-process tests use only fixture-owned ScreenTime.TestProcess and the ordinary Windows adapters. A 12-second test-only duration verifies grant, survival, second-instance denial, monitor restart with unchanged expiry, deadline termination and later denial; another fixture's same-name helper survives. Separate real-helper checks exercise injected Monday/Tuesday calendar time, downtime starting during grace, denied new instances, untouched Tuesday quota, and exit/expiry. Physical sleep/hibernate and a real-calendar midnight are not claimed; those boundaries use deterministic production seams.

The test-only duration is an internal initialization seam available to the test assemblies. It is persisted as the episode's deadline; restart never recomputes it. Production exposes no shorter-duration UI or environment setting. Setting SCREENTIME_PHASE4_FULL_DURATION=1 on the integration-test runner selects the actual default 20-minute duration for the full-duration helper check.

## Verification results

- Final `dotnet build --no-incremental`: succeeded, **1 warning, 0 errors**. The only warning is the pre-existing CS0618 for Model.MouseDown in DashboardWindow.xaml.cs.
- Final Core regression run: **174 passed, 0 failed, 0 skipped** (144 prior tests plus 30 Phase 4 cases).
- Full `dotnet test --logger trx` was executed. At that point Core had 172 cases, all passing; UI/integration had **15 passed, 20 failed, 0 skipped, 35 total**. The two subsequently added Core cases also passed in the final 174-case run.
- The UI failures are in existing interactive rule-editor/settings/first-run/dashboard tests: Windows SendInput reports Access is denied, with related focus failures and window timeouts. A focused first-run retry reproduced the denied keyboard input. LogonUI was observed in the same Windows session. The user was asked to unlock the desktop; no UI tests were removed, skipped, or rewritten to hide these failures. Full interactive UI verification remains incomplete until an unlocked-desktop rerun passes.
- Final rebuilt real-helper regression: **4 passed, 0 failed** (three Phase 4 cases, one retained Phase 3 helper case). An earlier run of these cases plus the first-run desktop-input probe had 4 passed / 1 failed; the failure was the legacy input probe.
- Full-duration helper validation: **1 passed, 0 failed**. The actual default duration was 00:20:00, starting **2026-09-24 08:44:53.1836695 UTC** and expiring **09:04:53.1836695 UTC** (04:44:53–05:04:53 EDT). The test made **1,184 survival checks**, preserved the deadline across monitor restart, denied the second helper, confirmed original termination within its three-second expiry assertion window, denied a later helper, and preserved the other fixture's helper. The entire scenario finished at 09:04:58.4367219 UTC after the later-launch denial. The copied-run artifact labels that final timestamp ConfirmedKilled; it is the scenario-completion time, not a measurement of original kill latency. The source label has been clarified to ScenarioCompleted.
- `git diff --check`: passed. All source changes are unstaged. No commit or push.
- Read-only preservation comparison: installed `%AppData%\TimeGuard` has the same three paths and SHA-256 hashes; all seven HKCU Run names/values match the baseline, including TimeGuard. No development startup registration was created.
- No Apex run, physical sleep/hibernate, real-calendar midnight, Phase 5 notification redesign or Phase 6 tray work was performed.

The 20-minute test runs from an immutable copy of the integration build so its processes and loaded assemblies can remain alive while deterministic tests and storage/edge-case hardening are completed. Final short helper scenarios validate the rebuilt source separately. Validation artifacts and TRX reports are retained under ignored `tests/TimeGuard.Tests/bin/Phase4Validation/`.

## Exact files modified

- `src/TimeGuard.Core/Models/DailyLog.cs`
- `src/TimeGuard.Core/Models/PolicyDecision.cs`
- `src/TimeGuard.Core/Properties/AssemblyInfo.cs`
- `src/TimeGuard.Core/Services/DatabaseMigrator.cs`
- `src/TimeGuard.Core/Services/DatabaseService.cs`
- `src/TimeGuard.Core/Services/IProcessMonitor.cs`
- `src/TimeGuard.Core/Services/IStateStore.cs`
- `src/TimeGuard.Core/Services/MonitorService.cs`
- `src/TimeGuard.Core/Services/UsageAccounting.cs`
- `src/TimeGuard.Core/Services/WindowsProcessMonitor.cs`
- `tests/TimeGuard.Tests/Phase2MigrationTests.cs`
- `tests/TimeGuard.Tests/Phase3MigrationTests.cs`
- `tests/TimeGuard.Tests/PolicyTestDoubles.cs`
- `tests/TimeGuard.UITests/Phase3HelperTests.cs`

## Exact files created

- `docs/PHASE4_IMPLEMENTATION.md`
- `src/TimeGuard.Core/Models/GraceEpisode.cs`
- `src/TimeGuard.Core/Services/GracePolicy.cs`
- `tests/TimeGuard.Tests/GraceTests.cs`
- `tests/TimeGuard.Tests/Phase4PersistenceTests.cs`
- `tests/TimeGuard.UITests/Phase4HelperTests.cs`

## git diff --stat

```text
 src/TimeGuard.Core/Models/DailyLog.cs              |  2 +
 src/TimeGuard.Core/Models/PolicyDecision.cs        |  4 +-
 src/TimeGuard.Core/Properties/AssemblyInfo.cs      |  1 +
 src/TimeGuard.Core/Services/DatabaseMigrator.cs    | 41 ++++++++++++--
 src/TimeGuard.Core/Services/DatabaseService.cs     | 65 ++++++++++++++++++++--
 src/TimeGuard.Core/Services/IProcessMonitor.cs     |  2 +
 src/TimeGuard.Core/Services/IStateStore.cs         |  2 +
 src/TimeGuard.Core/Services/MonitorService.cs      | 63 ++++++++++++++++++---
 src/TimeGuard.Core/Services/UsageAccounting.cs     | 63 +++++++++++++++------
 .../Services/WindowsProcessMonitor.cs              | 13 +++++
 tests/TimeGuard.Tests/Phase2MigrationTests.cs      |  4 +-
 tests/TimeGuard.Tests/Phase3MigrationTests.cs      |  2 +-
 tests/TimeGuard.Tests/PolicyTestDoubles.cs         |  1 +
 tests/TimeGuard.UITests/Phase3HelperTests.cs       |  2 +-
 14 files changed, 225 insertions(+), 40 deletions(-)
```

Git's unstaged diff statistics exclude the six new files listed above. No files were staged just to include them in the statistics.
