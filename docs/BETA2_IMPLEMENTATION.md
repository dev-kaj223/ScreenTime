# Beta 2 implementation evidence

This report describes the owner-authorized D25 refinement from integrated Phase 9 (`2d2b6e6c6eb365a11df99b7094599f3d342a39b1`). It is implementation evidence, not independent review, owner acceptance, or release approval. Earlier phase reports remain historical. No Core policy, accounting, grace persistence or exact-instance termination path changed.

## Delivered behavior

Successful first-run password setup opens initial configuration once. Later Settings and Exit still authenticate; cancelled and wrong credentials do nothing. Tray left click opens/activates one read-only Dashboard. Dashboard navigation cancels an open protected modal before activating the read-only view, so an existing Dashboard is usable rather than disabled by WPF's modal loop.

Right click toggles a temporary interactive, frameless, taskbar-free status popup. Escape, click-away and another right click dismiss it; its footer opens Dashboard or the existing protected Settings/Exit commands. Close/deactivation is guarded against reentrant close. The popup anchors beside the invoking cursor and clamps to that monitor's work area; it stays above Explorer's overflow while open, with the invoking icon clear. Passive notifications retain their separate nonactivating, input-transparent rendering. Explorer can dispatch deactivation before NotifyIcon's mouse-up callback: a bounded same-position/same-second deactivation record consumes that repeated right click. Explicit Escape/footer closes do not set that record.

Dashboard and popup use copied Core status facts for allowance, usage, downtime, future availability and original grace deadline/countdown. Display-only formatting uses explicit AM/PM, including notices and session drilldown. Unknown availability remains unknown. Dashboard Today cards and the existing seven-day chart are bounded, read-only and restricted to enabled configured apps; future/unrelated history is excluded. No new analytics or passive tracking subsystem is introduced.

The daily editor has seven hours/minutes pairs (zero means Unlimited). Downtime has multi-weekday selection, hour/minute/AM-PM controls, explicit next-day periods and grouped edit/remove rows. The editor preserves integer-minute limits, disabled intervals, normalized policy and retained legacy values. Deferred aggregate cap and forced-break controls are removed; stored fields remain compatible. A fresh Settings view has an honest empty recent-apps message.

The app's version is `0.1.0-beta.2`; SDK informational metadata includes source revision. Packaging instructions describe the new entry points. The package verifier retains strict default preservation checks and adds an explicit exact-live-owner mode that reports production bytes as not compared while checking retained process survival, TimeGuard/Development bytes and startup values. It also checks missing-native-library failure before a disposable test profile is created. The existing canonical UTF-8/LF publish/reproducibility path is unchanged.

## Requirement-to-test mapping

| Requirement | Evidence |
| --- | --- |
| Initial configuration without redundant auth; subsequent protection | `FirstRunWindowTests`, `Beta2PresentationTests`, `SettingsWindowTests`, popup footer tests |
| Read-only singleton Dashboard and continued enforcement | `DashboardWindowTests`, Settings existing/new Dashboard cases; an owned exhausted helper is still terminated after Dashboard closes |
| Real tray left/right behavior, focus, dismissal and lifecycle | `Beta2PopupTests` physically clicks the uniquely labeled fixture icon, compares hit UIA ancestry to that exact icon, checks native foreground/styles, repeat-right-click, Escape/immediate reopen, click-away and footer protection |
| Safe native inputs beside a live owner app | Fixture footer clicks require actual `WindowFromPoint` root HWND and process ownership before input; missing/obscured identity fails instead of clicking |
| Work-area placement and list size | Actual corners of each available monitor in `Beta2PopupTests`; many-app scroll/clipping in `TrayStatusTests` |
| AM/PM and truthful presentation | `Beta2PresentationTests`, `NoticePresentationTests`, `TrayPresentationTests`; culture-independent midnight/noon, durations and unknown availability |
| Daily/downtime conversion and storage | 26 pure `RuleEditorDraftTests` plus 8 real `RuleEditWindowTests`: every clock minute, zero/max/bounds, disabled/original representation, grouped weekdays, Sunday crossing, full day, adjacency/half-open behavior, DST, save/reload |
| Retained cap/break compatibility and hidden UI | Rule editor model and Settings storage/UI regressions |
| Accepted passive notice behavior | Full `NotificationWindowTests`: six actual notice kinds, foreground, keyboard, click-through, native styles, measured duration and persisted preferences |
| Actual packaged UX | Extended `ReleaseSmokeTests`; these remain opt-in and must run against the reviewed Beta 2 archive before release |

Named event hooks exercise the same app commands in disposable Test profiles for routine lifecycle tests. They do not substitute for the separate physical tray-click/native-focus tests. No test targets real games, installed profiles, or name-wide process termination.

## Validation and captures

The nonincremental Release build succeeded with only the pre-existing OxyPlot `Model.MouseDown` obsolescence warning. The final incremental build passed without warnings. Commands used `dotnet build TimeGuard.sln -c Release --no-restore` (also `--no-incremental` for the clean rebuild), then `dotnet test TimeGuard.sln -c Release --no-build --no-restore --logger trx` with an ignored output directory.

The final production binary's full run (`FullFinal`, 2026-09-25) passed Core **223/223**; UI **103 passed, 5 failed, 4 opt-in package cases skipped** (112 total). One UI failure read an empty popup tree immediately after window discovery; four subsequent Settings cases timed out on a shared fixture. The test-only correction waits for the exact popup and both expected rows without reopening it, gives each Settings test an independent fixture, and explicitly repeats both modal-navigation paths three times in one process with fresh protected Settings access after each close. The affected classes then passed **12/12** (`ReadinessFinal`). A further focused navigation run passed **2/2** (`NavigationFinal`), including the original legacy-cap database-write interleave in every iteration. The final corrected full UI run on `f72bc4b` passed **108/108 runnable tests**, with the four actual-package cases skipped (`FinalIsolated`, 7m02s); its result is recorded in PR #6. These are full-run plus targeted-remediation results, not a claim that the original full run was green. The original timeout cause was not reproduced in the repeated same-process regression; independent review should assess this limitation.

The focused final popup run passed 3/3 (`NativeHitFinal`, 2026-09-25). It exercised the one physical monitor available; it does not establish a mixed-DPI/multi-monitor hardware matrix. The final notice/Settings/popup run passed 11/12 before the stricter shell hit check learned to recognize an anonymous glyph child by exact ancestor identity; the corrected physical popup cases then passed. The passive notice run checked 371 samples and all six durations (about six seconds each; sixty seconds for the long notice), preserving the fixture helper's foreground and mouse/key counters throughout. The helper is normal, non-topmost before any notice measurement; temporary setup elevation affects only that owned helper and is always removed.

Exploratory validation exposed and corrected an actual popup reentrant-close crash, Explorer repeat-click ordering, overflow/footer overlap, modal Dashboard disablement, and fixture lookup/input preconditions. Earlier failed runs are retained locally rather than described as passing evidence.

Actual captures were reviewed locally: `beta2-native-popup.png`, empty/populated Dashboard, daily/downtime editor, and many-app/grace status. The popup footer is unoccluded and styled; the editor's Save/Cancel remain outside its scroll area; grouped schedules and AM/PM controls are readable. Captures and TRX/notice diagnostics remain outside version control under the UI test output directory. They contain disposable fixture data only.

The owner's live Beta 1 process was preserved during validation. Its active/locked database bytes are not claimed unchanged. No Beta 1 artifact or draft release was modified. The owner-reported Beta 1 gaming/performance feedback is observation, not newly instrumented proof.

## Remaining delivery gates

Independent exact-head review, GitHub CI, any reviewer-required owner UX gate, final clean-source packaging and independent archive reproduction, actual-package smoke (actual persisted twenty-minute grant/restart identity plus explicitly seeded near-expiry; an elapsed twenty-minute run remains optional unless review requires it), and the unsigned draft Beta 2 release remain subsequent gates. This report does not claim an unperformed Apex run, package test, review or merge.

## Independent-review remediation B2-R1

Review `5321839208` on PR #6 identified a real unowned ProcessPicker continuation after Settings was closed by Dashboard navigation. The picker and native delete confirmation now have Settings as owner; every editor/picker/delete continuation checks that Settings is still visible before mutating or opening another editor. Dashboard navigation explicitly closes the deepest owned WPF modal first and queues the next navigation step, allowing each ShowDialog frame to unwind before closing Settings. Native owned confirmations close with their owner.

Actual nested picker/editor/delete-confirmation regression cases passed **3/3** (`NestedModalPolicy`). They invoke the same left-tray command via the isolated Dashboard event, verify all protected windows close and the existing Dashboard becomes interactive, assert no rule changes, authenticate afresh, select an exact owned helper in a new picker, cancel the resulting editor and another picker, and verify a newly launched owned helper is denied by current downtime after all views close. Ownership alone initially removed WPF windows but left the modal/authentication frame busy; that reproduced failure drove the innermost-first unwind correction. Test setup uses a published downtime observation rather than trying to replace live Core accounting through an external database write. Affected regression validation and exact-head re-review remain pending at this section's writing time.

## Independent-review validation correction B2-V1

Reviewer review `5322023301` accepted B2-R1's production correction but retained one failure from its 32-case native suite: the nested editor case failed only at the final all-windows-closed assertion after all navigation/fresh-auth/selection assertions passed. The original remaining-window identity was unavailable. Reviewer diagnostic-only nested cases (3/3) and five editor repetitions passed, so neither a product cause nor a definitive cause for that original failure is claimed.

The test now captures the exact Settings and Dashboard HWNDs, waits for Settings to disappear and that Dashboard to become enabled, then closes Dashboard. Its strict zero-remaining-windows assertion remains; both steps retain title/HWND/enabled/PID and process-survival diagnostics. No unknown window is excluded, and the later fresh status and exact-helper enforcement assertions remain unchanged. Production source is unchanged by this correction.

Build passed (baseline OxyPlot warning only). The corrected nested picker/editor/native-confirmation cases passed **3/3** (`ReviewReadiness`, 2026-09-25). Their diagnostics show the expected enabled Dashboard alone after Settings closes, no windows after Dashboard closes, and subsequent published downtime plus exact-owned-helper denial. The known owner Beta 1 process survived with its original creation identity. Exact-head reviewer disposition and package gates remain pending.
