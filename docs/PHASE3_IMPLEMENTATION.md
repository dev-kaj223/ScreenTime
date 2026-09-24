# Phase 3 implementation review

Implemented explicit downtime, measured selected-application accounting, and local midnight/power-event handling only. The architecture plan and both earlier implementation reports were read before editing. Phase 1 profile isolation and supervised lifecycle, Phase 2 pure policy and exact-instance enforcement remain in place. No commit or push.

## Downtime and policy

`BlockedPeriod` holds Id, RuleId, StartDayOfWeek, StartMinute, EndMinute, EndDayOffset (0 or 1), and Enabled. A period lasts more than zero and at most 24 local-calendar hours. Minute values are 0–1439; weekdays are 0–6. Equal same-day endpoints are invalid; equal endpoints with next-day selected represent a full day. Validation runs at the Core service/model boundary and in database constraints, not just WPF.

`DowntimeEvaluator` is the single authority for expansion, cross-midnight/week wrap, overlap/adjacency merging, active downtime end, next downtime start, and next launch availability. Definitions remain separate in storage/editor even when evaluation merges them. Intervals are half-open: blocked exactly at start, available exactly at end subject to other facts. Sunday 22:00–Monday 08:00 works across the week boundary.

Availability projection assumes no additional use and consults persisted quota for each candidate local date. It searches the following weekly cycle (through eight days), skips exhausted dates, and skips merged downtime. Continuous full-week downtime returns no scheduled end/availability rather than the artificial end of an expansion horizon. Next availability can differ from the current downtime end when quota is exhausted. This is a bounded projection, not a guarantee against future configuration changes or new use.

Time zones are explicit dependencies. Invalid spring-forward local boundaries advance to the first valid minute; ambiguous fall-back starts use the earlier instant and ends use the later instant. A repeated interval therefore covers both occurrences. UTC measures elapsed intervals; local dates determine allowance buckets. Zone changes rebase accounting and reevaluate the new local day. No clock-tamper lockout is introduced.

Policy facts no longer contain allowed-window endpoints. States are Available, TemporaryDowntime, Warning, and DailyQuotaBlocked. Downtime and exhausted-quota reasons are retained simultaneously; downtime is the primary display state during overlap. Launch and continuation permissions are separate decision fields, currently both denied by either restriction. This leaves room for Phase 4 to apply a captured-instance continuation exception without granting launches or new-day quota. There is no grace state or exception in this implementation.

`Blocked` is legacy data only. It is preserved for compatibility, never set by downtime or used for launch permission. Settings' current blocked display now uses the same Core policy instead of that stored flag. Existing five-minute warning behavior and popup presentation remain unchanged.

## Schema version 2 and conversion

The upgrade from `PRAGMA user_version=1` to **2** adds:

- `BlockedPeriods`, with a cascading RuleId foreign key, index on (RuleId, StartDayOfWeek), integer/range/boolean/duration checks.
- `DailyUsage.ObservedSeconds` and `DailyUsage.QuotaSeconds`, nonnegative integer columns.

Foreign keys are enabled on every service connection and on migration connections. Existing version-zero upgrades still apply the Phase 2 canonicalization/uniqueness work. Schema additions, data conversion and version changes share a transaction; invalid legacy schedules roll everything back. A consistent SQLite `<database>.pre-phase3.bak` is retained before upgrading existing data; version-zero upgrades also preserve the Phase 2 backup discipline. Fresh databases need no backup. Reopening version 2 is idempotent; future versions are rejected.

The installed `%AppData%\TimeGuard` path is explicitly rejected before the database is opened. The legacy-production launch route consequently cannot run this new schema against the installed database. Only isolated ScreenTime test profiles were migrated during validation; no installed TimeGuard database was queried or migrated, and the default ScreenTime-Dev profile did not need to be opened. Read-only file hashing is used separately to check preservation.

Existing weekday allowed windows are converted once to their complement. The legacy end comparison was inclusive at its exact instant, whereas the new model has minute boundaries. Conversion rounds the permitted end outward to the **next minute**, adding less than one minute of permitted time. Thus 17:00–23:59 becomes downtime [00:00,17:00), and 08:00–17:00 becomes [00:00,08:00) plus [17:01,next midnight). Equal legacy endpoints preserve their minute; reversed, malformed or unpaired legacy endpoints cause an explicit migration failure instead of inventing a meaning. Null pairs generate no periods. Old window columns remain readable for compatibility; changing them after migration has no policy effect. The editor saves allowances and explicit periods.

Legacy REAL minutes convert once using SQLite nearest-second rounding of the stored value times 60, clamped at zero; both initial second counters receive that result. Binary floating-point values near an exact halfway point follow SQLite's result. Previously accumulated history cannot distinguish actual observation from charge, so these migrated counters are only an initial approximation. Runtime policy reads integer quota seconds exclusively. The `UsageMinutes` property is a compatibility projection/setter, and legacy UsageMins writes mirror QuotaSeconds/60 for existing charts. There is no read/round/write cycle in live accounting.

Usage for all affected dates in a coordinator observation is saved in one transaction before termination. A failure faults the supervised monitor rather than silently continuing with uncommitted accounting. No grace, notification receipt, break-state, or overall-cap tables were added.

## Measured accounting

`UsageAccounting` uses `TimeProvider.GetTimestamp/GetElapsedTime` and current UTC/local-calendar context. The first observation charges zero. At least one exact process identity must survive from the previous observation into the current one. Multiple instances count once per configured application, including minimized/background runtime; unrelated applications are excluded. A process exit, replacement/PID reuse, or first sighting does not fabricate earlier play. Runtime before first detection, after the final observation, or between observations with no surviving identity is not inferred; polling can therefore undercount those uncertain tails. Confirmed termination removes that identity from the accounting anchor.

An ordinary delayed tick uses its measured duration (tests include 3, 7, 19 and 30 seconds). Intervals longer than **30 seconds** are treated as unobserved gaps and rebase without charging. This deliberately bounds inference from polling. Explicit suspend/resume also rebases even for shorter sleeps. Wall/monotonic discrepancies over one second or non-forward time rebase; smaller clock adjustments map measured elapsed proportionally across known calendar boundaries. Configuration reload rebases rather than retroactively reclassifying the preceding interval.

Each continuous observed interval is split at midnight and downtime starts/ends. Observed seconds include confirmed continuous runtime even when enforcement fails. Quota seconds include only permitted portions, clamped to the day's remaining allowance at exhaustion; excess observed seconds never consume additional quota. A denied first launch has zero elapsed charge, and continuous downtime never consumes quota. For 16:59:58–17:00:03 with downtime ending at 17:00, observation is five seconds and charge is three. For downtime beginning at 17:00, charge is two.

Fractional ticks are carried separately per app/date/classification and only whole seconds are persisted. This prevents repeated per-poll rounding drift. Less than one second per counter/date may remain uncommitted at process shutdown, restart or a zone-context replacement; fractions never leak into another date. Restart deliberately begins with a fresh observation anchor, retaining committed quota but reconstructing no runtime during the outage. Existing session-history timestamps remain compatibility history and are not the accounting authority.

## Midnight, sleep and coordinator timing

Midnight materializes the enabled application's new local-date ledger at zero; it does not grant permission. With Tuesday downtime [00:00,08:00) and [08:00,17:00), Monday's final permitted seconds remain on Monday, Tuesday quota stays untouched, and availability remains denied continuously until Tuesday 17:00. Without the second period, availability returns at 08:00 if quota permits. Daily allowances remain configurable per weekday; tests exercise Mon–Thu 60, Fri 90, Sat/Sun 120 and changed values.

`PowerSessionHelper` subscribes to Windows suspend/resume and time-change events. Callbacks only queue rebase/wakeup signals; the existing serialized coordinator owns observation, persistence and enforcement. Resume wakes that coordinator immediately, with current day/downtime/quota reevaluation and no sleep charge. Time changes clear the cached system zone and queue reevaluation. Subscriptions are disposed on shutdown.

Ordinary discovery remains about five seconds. The same loop waits until the earlier of discovery, merged downtime boundary, local midnight, or calculable remaining-quota exhaustion. A semaphore handles configuration/power wakeups. There is no second enforcement timer, high-frequency discovery loop, task scheduler, process injection, or game integration. Polling observes launches after they happen; detection/enforcement latency and very short unseen executions remain inherent limitations. Quota enforcement remains immediate Phase 3 termination once exhaustion is observed, with subsecond fractions potentially delaying the whole-second boundary by less than one second.

## Editor changes

The seven daily allowance fields remain. The old per-day From/To fields are removed. A day selector, HH:mm start/end fields, Next day checkbox, Add period button, list, and Remove period button configure multiple independent periods. Editing a period means removing it and adding its replacement. Existing disabled periods retain their enabled state on roundtrip. Invalid formats, reversed/equal same-day times, and excessive duration produce explicit errors. Existing styling, break fields, protected settings flow, and the remainder of the settings UI remain intact. Rule list summaries now show downtime.

## Tests and validation

Deterministic Core coverage includes exact endpoints, same-day/midnight/cross-midnight/week-wrap, overlap/adjacency, disjoint/disabled/no/all-week periods, invalid values, next availability, simultaneous quota/downtime, configurable weekdays, zero first charge, delayed ticks, continuous identities, unrelated-app exclusion, fractional carry, quota clamping, midnight splits, long gaps, short suspend/resume, sleep across midnight, immediate resume wakeup, clock adjustments, DST, and zone changes. Migration tests cover backup, one-time conversion/rounding, transactional rollback, constraints/foreign keys, period roundtrip/delete cascade, atomic multi-date checkpoints, idempotence, future-version rejection, and installed-profile rejection.

The WPF tests cover the actual weekly/downtime editor, invalid entry, multiple-period add/remove, overnight persistence, and all previous settings/popup/isolation cases. An early version of the new list test immediately counted two consecutive identical additions; UI Automation reported only two rows. The corrected test waits for each addition and uses a distinct third period for the removal check; the focused rerun passed. Its first failure left an editor open and caused three cascading assertions in that run; the final verification below is from a fresh complete run.

The real-process Phase 3 integration check uses only fixture-owned headless `ScreenTime.TestProcess` instances, the ordinary Windows discovery/termination adapters, and the actual serialized worker. An injected UTC clock runs continuously from Tuesday 16:59:56 at real stopwatch speed. Adjacent periods end at 17:00 four seconds later. The test verifies denial kills the helper with quota unchanged at 53 seconds, availability reopens, a new helper accumulates measured time to 60 seconds and is terminated, and another fixture's same-name helper survives. No destructive Apex or ranked testing was performed.

**Manual validation limit:** physical laptop sleep/hibernate and a real wall-clock midnight were not performed. Those transitions were exercised deterministically through the production coordinator's power/time seams; the real helper test uses a running injected clock. Hardware delivery of Windows power events still needs a manual device check. No claim of completed hardware sleep validation is made.

## Deliberately updated earlier tests

- `RulesEngineTests`: existing window scenarios now supply explicit downtime. The old inclusive end case explicitly expects denial at the new blocked start, while end-of-downtime remains available. Quota, warnings, disabled rules, purity, relaunch, and simultaneous reasons remain covered.
- `PolicyStateTests`: outside/inside conversion uses downtime; the old first-sample fixed-five-seconds assertion becomes zero at the first sample followed by seven measured seconds. Same-app aggregation and unrelated-app exclusion remain asserted.
- `PolicyTestDoubles`: fake time supplies deterministic monotonic timestamps and an explicit zone.
- `EnforcementServiceTests`: snapshot construction matches the new facts; exact-instance safety assertions are unchanged.
- `Phase2MigrationTests`: latest version is now 2. Legacy fixtures explicitly remove Phase 3 additions before marking themselves version zero; canonicalization/backup/rollback assertions remain.
- `RuleEditWindowTests`: weekday persistence now asserts explicit downtime instead of obsolete allowed fields. Save uses UI Automation invocation for controls below the scroll viewport. New button-action checks wait for observable list changes.

No existing test body was deleted. Legacy storage/window-column roundtrip tests remain unchanged because those columns still exist as compatibility data.

## Deferred functionality

No Finish Current Session, 20-minute grace, GraceEpisode/GraceProcesses, grace deadline/persistence, grace-over-midnight exception, grace accounting, ten-minute warning, passive/nonactivating notice, popup redesign, system tray/status panel, Task Scheduler, TimeGuard import, break persistence, or overall-cap ledger. Existing exact-instance termination and UI-independent enforcement are preserved.

## Final verification

- Final `dotnet build --no-incremental`: succeeded, **1 warning, 0 errors**. The warning remains the existing CS0618 for `Model.MouseDown` in `DashboardWindow.xaml.cs`; no new warning was introduced.
- Final `dotnet test`: **176 passed, 0 failed, 0 skipped** — **144 Core/unit tests and 32 UI/integration tests**. The UI run includes real owned-helper enforcement and the new editor tests.
- `git diff --check`: passed.
- Installed TimeGuard preservation: all three file paths/SHA-256 hashes and all seven HKCU Run name/value pairs matched the read-only baseline, including TimeGuard's registration.
- `git status`: branch `screentime-dev`, **20 modified files and 10 new files**, all unstaged. No commit or push.
- Physical sleep/hibernate and real-calendar midnight remain the explicitly stated manual validation limitation above; deterministic boundary tests and the continuously clocked real-helper check passed.

## Exact files modified

- `src/TimeGuard.App/UI/App.xaml.cs`
- `src/TimeGuard.App/UI/RuleEditWindow.xaml`
- `src/TimeGuard.App/UI/RuleEditWindow.xaml.cs`
- `src/TimeGuard.App/UI/SettingsWindow.xaml`
- `src/TimeGuard.App/UI/SettingsWindow.xaml.cs`
- `src/TimeGuard.Core/Models/AppRule.cs`
- `src/TimeGuard.Core/Models/DailyLog.cs`
- `src/TimeGuard.Core/Models/PolicyDecision.cs`
- `src/TimeGuard.Core/Models/PolicySnapshot.cs`
- `src/TimeGuard.Core/Services/DatabaseMigrator.cs`
- `src/TimeGuard.Core/Services/DatabaseService.cs`
- `src/TimeGuard.Core/Services/IStateStore.cs`
- `src/TimeGuard.Core/Services/MonitorService.cs`
- `src/TimeGuard.Core/Services/RulesEngine.cs`
- `tests/TimeGuard.Tests/EnforcementServiceTests.cs`
- `tests/TimeGuard.Tests/Phase2MigrationTests.cs`
- `tests/TimeGuard.Tests/PolicyStateTests.cs`
- `tests/TimeGuard.Tests/PolicyTestDoubles.cs`
- `tests/TimeGuard.Tests/RulesEngineTests.cs`
- `tests/TimeGuard.UITests/RuleEditWindowTests.cs`

## Exact files created

- `docs/PHASE3_IMPLEMENTATION.md`
- `src/TimeGuard.App/Helpers/PowerSessionHelper.cs`
- `src/TimeGuard.Core/Models/BlockedPeriod.cs`
- `src/TimeGuard.Core/Services/DowntimeEvaluator.cs`
- `src/TimeGuard.Core/Services/UsageAccounting.cs`
- `tests/TimeGuard.Tests/DowntimeEvaluatorTests.cs`
- `tests/TimeGuard.Tests/MidnightTests.cs`
- `tests/TimeGuard.Tests/Phase3MigrationTests.cs`
- `tests/TimeGuard.Tests/UsageAccountingTests.cs`
- `tests/TimeGuard.UITests/Phase3HelperTests.cs`

## git diff --stat

```text
 src/TimeGuard.App/UI/App.xaml.cs                 |  4 ++
 src/TimeGuard.App/UI/RuleEditWindow.xaml         | 31 ++++-----
 src/TimeGuard.App/UI/RuleEditWindow.xaml.cs      | 68 ++++++++-----------
 src/TimeGuard.App/UI/SettingsWindow.xaml         |  2 +-
 src/TimeGuard.App/UI/SettingsWindow.xaml.cs      | 11 ++-
 src/TimeGuard.Core/Models/AppRule.cs             | 20 +-----
 src/TimeGuard.Core/Models/DailyLog.cs            |  8 ++-
 src/TimeGuard.Core/Models/PolicyDecision.cs      |  7 +-
 src/TimeGuard.Core/Models/PolicySnapshot.cs      | 32 +++++----
 src/TimeGuard.Core/Services/DatabaseMigrator.cs  | 85 ++++++++++++++++++++---
 src/TimeGuard.Core/Services/DatabaseService.cs   | 39 +++++++++--
 src/TimeGuard.Core/Services/IStateStore.cs       |  1 +
 src/TimeGuard.Core/Services/MonitorService.cs    | 86 ++++++++++++++++++------
 src/TimeGuard.Core/Services/RulesEngine.cs       | 17 +++--
 tests/TimeGuard.Tests/EnforcementServiceTests.cs |  4 +-
 tests/TimeGuard.Tests/Phase2MigrationTests.cs    |  5 +-
 tests/TimeGuard.Tests/PolicyStateTests.cs        | 14 ++--
 tests/TimeGuard.Tests/PolicyTestDoubles.cs       |  6 +-
 tests/TimeGuard.Tests/RulesEngineTests.cs        | 24 +++----
 tests/TimeGuard.UITests/RuleEditWindowTests.cs   | 77 ++++++++++++++++++---
 20 files changed, 371 insertions(+), 170 deletions(-)
```

This command reports tracked changes only. Ten new files are listed above; Git omits them from diff statistics until staged. All changes remain unstaged for review.
