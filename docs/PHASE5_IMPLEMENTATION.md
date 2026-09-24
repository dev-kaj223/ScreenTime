# Phase 5: passive gaming notices

Phase 5 only, on owner-requested `phase5-gaming-safe-notifications`, based on `screentime-dev` at `6737bae3a714bd9c971bb6a160b5d47c2eeda90e`. The task explicitly stops with an open PR for independent review; it does not authorize merging. This report describes the minimal WPF candidate and distinguishes helper validation from the owner's pending Apex acceptance.

## Implementation and authority

The interactive `WarningPopup` and `BlockedPopup` are removed. `PassiveNoticeWindow` is the single automatic notice surface. It has static text, no buttons, links, close controls, acknowledgement, animation, input registration or game integration. `App` consumes Core's bounded notification mailbox without invoking policy or persistence from WPF. No tray/status/protected-action work is included.

`NotificationPolicy` derives candidates from the current daily quota and committed `PolicyDecision.Grace`. The sequence is approximately ten and five minutes of daily quota remaining, grace start, then five minutes of grace remaining. First observation below a threshold may emit the current notice. Skipped milestones coalesce to the most urgent current milestone. Grace recovery formats the original absolute deadline and actual remaining minutes; it never creates an episode or calculates a replacement deadline. The text states quota exhaustion, temporary continuation for the captured session, denial of new launches, and the fixed local stop time. The optional blocked notice distinguishes downtime from quota and includes projected next availability when known.

The Phase 4 coordinator still commits usage/episode state, evaluates exact instances and enforces before publishing notice candidates. `NotificationOutbox` owns bounded best-effort receipt IO on a separate background task. Even a stalled receipt operation cannot hold up the coordinator. It coalesces pending requests by app (at most eight); its delivery channel also holds at most eight. WPF polls only that mailbox every 250 ms and coalesces delivery to one surface. It never enumerates game processes or reads SQLite. A receipt/display failure suppresses that attempt; it cannot fault policy, grant time, change a capture set or delay deadline enforcement. Orderly shutdown observes the receipt task.

For prior-phase compatibility, the existing warning flag and legacy Core callback tests remain; the application no longer subscribes to those callbacks. The new notification route neither reads nor uses `WarningSent` as a receipt. Existing quota/grace/downtime accounting and Windows enforcement adapters are unchanged.

## Receipts, stale requests and migration

Schema **4** adds only `NotificationReceipts(ReceiptKey, AppKey, Kind, RequestedAtUtcTicks)`. Receipt keys are canonical app/local quota date/milestone for quota notices and immutable episode ID/milestone for grace notices. Keys survive rule deletion/recreation. A selected five-minute milestone also suppresses an earlier ten-minute request after a same-day allowance increase; the analogous rule applies to final grace versus grace start. Receipt rows mean **selected for best-effort delivery**, never proof of display or acknowledgement. Runtime attempts prevent ordinary ticks from retrying the same current candidate after receipt failure. Blocked notices use a runtime thirty-second per-app cooldown and do not accumulate durable launch history.

The existing versioned migration transaction and consistent SQLite backup discipline now extend through version 4; existing databases get `<database>.pre-phase5.bak`, fresh databases do not. Unknown newer versions are rejected. A collision rolls back the schema/version and preserves the backup. Prior migration fixtures explicitly remove the new table before emulating older versions. No installed TimeGuard profile is opened or migrated.

Receipts are deliberately separate from the authoritative usage/grace commit: a notification-storage failure must not prevent a grant or enforcement. A crash after grace commit but before receipt may lead to a recovery notice from that same episode, with its original deadline. A crash after receipt but before display may lose the notice. This is best-effort external delivery, not an exactly-once display guarantee. Notification code has no API for writing grace.

Requests expire after fifteen seconds (or earlier at the grace deadline). Core rechecks the current candidate and configuration revision before receipt/display and WPF checks again throughout visibility. Reload invalidates queued notices. Quota exhaustion, a newer milestone, confirmed exit, expiry and sleep past validity discard stale requests. No backlog of old notices is replayed. `NotificationFacts`, `Decisions` and `GraceEpisodes` expose read-only facts suitable for future Phase 6 status.

## Focus and input design

`ShowActivated=false`, `Focusable=false`, disabled WPF hit testing and no keyboard tab navigation are established before showing. Source initialization applies `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `WS_EX_LAYERED`, and `WS_EX_TRANSPARENT` to ScreenTime's own HWND. `WM_MOUSEACTIVATE` returns `MA_NOACTIVATE`; `WM_NCHITTEST` defensively returns `HTTRANSPARENT`. Cross-process pass-through relies on the layered-window/transparent-style combination, rather than WPF hit testing or `HTTRANSPARENT` alone. See Microsoft's [layered-window documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) and [extended styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles).

The small surface is briefly topmost, positioned with `SWP_NOACTIVATE` in the foreground window's monitor work area using native pixel bounds and WPF DPI conversion. It is never owned by, attached to, or used to activate the game. It has no taskbar entry. The implementation does not call Activate/Focus/SetForegroundWindow or register input/controller hooks. Exclusive-fullscreen visibility is not guaranteed, and the implementation does not attempt to force visibility by changing fullscreen state. Mixed-DPI/monitor configurations remain a manual target-hardware check.

A presentation-only timer closes the notice after approximately six seconds, using monotonic elapsed time plus a UTC upper bound so a backward clock adjustment cannot prolong the surface. Closed stops the timer; invalidated notices can close sooner. An unresponsive WPF dispatcher can delay visual dismissal until it resumes; enforcement continues independently. This limitation is part of the WPF experiment and must not be represented as an unconditional native timeout guarantee.

`--notice-diagnostics` opts into local bounded `notice-diagnostics.jsonl` (1 MiB plus one rotated file). Samples contain timestamp, notice HWND, foreground before/after, the WPF thread's active/focus HWNDs, WPF keyboard-focus/mouse-capture booleans and extended styles. There are no window titles, keyboard contents or game content. Production defaults do not sample/write these diagnostics. Samples are observations at show and 250 ms intervals, not proof that no sub-sample transition ever occurred.

## Tests and current evidence

`NotificationTests` covers the complete milestone sequence, repeated polling, restart receipts, fixed episode/deadline, late recovery with actual remaining grace, skipped/stale requests, configuration invalidation, receipt failures, a deliberately stalled receipt writer, continued relaunch/downtime denial and hard enforcement at the original deadline. Schema tests cover version-three backup, idempotence, collision rollback and grace preservation. Prior Phase 1–4 Core assertions remain, with only latest-schema fixture/version adjustments.

`PopupTests` retains real owned-helper denial/relaunch assertions while replacing the intentionally removed OK-button behavior with no-button and automatic-dismissal assertions. `NotificationWindowTests` displays all five notice kinds through the production renderer against a maximized, separately running fixture-owned helper. It checks native styles/messages, foreground/active/focus samples, lack of buttons, real mouse/keyboard delivery to the helper through the opaque notice area, non-minimization and measured five-to-eight-second dismissal. The helper writes only input counts. Preview mode is also asserted to create no database.

Validation on 2026-09-24, Windows/.NET 8, isolated profiles:

- `dotnet restore TimeGuard.sln`: passed using the existing NuGet configuration; initial sandbox access required an approved retry. No credential/configuration changes.
- `dotnet build TimeGuard.sln --no-restore --no-incremental -c Release`: passed, zero errors; only the baseline `DashboardWindow.xaml.cs` CS0618 warning.
- `dotnet test tests/TimeGuard.Tests/TimeGuard.Tests.csproj --no-build -c Release --logger trx`: **183 passed, zero failed/skipped** (174 prior Core cases plus nine Phase 5 cases). Final artifact directory: `tests/TimeGuard.Tests/bin/Phase5Validation/FinalCore`.
- Focused real-helper/passive-block regression: **7 passed**, covering Phase 3, Phase 4 and updated PopupTests. These use only fixture-owned `ScreenTime.TestProcess` identities.
- Corrected native/input probe: **1 passed**, all five notice kinds. **130 samples**, helper foreground unchanged; no notice active/focused, no keyboard focus/mouse capture, native style/message checks pass, five clicks and five key presses received by the separate helper, no minimization. Measured visible durations: **5.993, 6.027, 6.025, 6.007, 6.007 seconds**. This setup reported 420×190 notice bounds; this is not mixed-DPI/exclusive-fullscreen or controller evidence.
- The initial full UI run had **35 passed / 1 failed** in the new input probe. Investigation found the probe's counter-file reader denied concurrent writes by its own helper, allowing a helper IO-error dialog to invalidate foreground/input observations. The test reader now permits write-sharing and tolerates partial reads. Earlier failure records remain local; production activation behavior was not changed to hide this test failure. The focused corrected probe passed all input/focus assertions.
- Final `dotnet test tests/TimeGuard.UITests/TimeGuard.UITests.csproj --no-build -c Release --logger trx`: **36 passed, zero failed/skipped**, including all prior UI/integration scenarios and the new five-kind native/input probe. Duration 3m18s. Final artifact directory: `tests/TimeGuard.Tests/bin/Phase5Validation/FinalUI`. All desktop runs were serialized.
- `git diff --check` and `git diff --cached --check`: passed. Manual preview PowerShell example: parser syntax check passed.
- Read-only preservation comparison: **three installed TimeGuard file paths/hashes unchanged; all seven HKCU Run names/values unchanged**. No installed profile, registration or live application was used as a destructive fixture.

Local TRX/diagnostic artifacts stay under ignored build output and are not included in the PR. The current short real-process tests and deterministic original-deadline checks are rerun; the real twenty-minute helper gate from Phase 4 is not rerun because the grace duration, accounting and deadline algorithm are unchanged. Its historical result is not presented as a Phase 5 run. No physical sleep/hibernate, real-calendar midnight, controller hardware, Apex, frame-time/performance budget or mixed-display-mode acceptance is claimed.

## Owner's manual Apex acceptance procedure

**Prerequisite:** clean helper/input validation on an unlocked interactive desktop. No automated WPF result establishes Apex safety. Preserve the installed TimeGuard profile/process/startup state. If another enforcer is already targeting Apex, coordinate that separately; this task does not authorize stopping it. This test must never exercise destructive enforcement against Apex.

1. Use Apex **firing range/training only**. Record Windows build, GPU/driver, display resolution/refresh, scaling and monitor layout, game mode (borderless/exclusive where supported), controller/input device and built-in performance display configuration. Record idle and active gameplay baseline with game-provided performance information; do not add injected capture/graphics tools.
2. Launch the notice-only preview below with a fresh test profile. It does **not** construct a database, monitor, process observer or terminator. It displays synthetic ten-minute, five-minute, grace-start, final-grace and blocked text through the same production WPF surface. The displayed times are preview examples, never actual Apex deadlines.
3. Return to the firing range before the delay elapses. For the first and subsequent notices, verify no focus loss, Alt-Tab/minimize/fullscreen transition, held-key interruption, mouse interception, controller interruption, or obvious/reproducible frametime spike. Exercise mouse input beneath several opaque/text locations of the notice. Record visible duration, before/during/after foreground HWND samples and the game's actual fullscreen/minimized behavior.
4. Repeat the sequence for supported display modes/layouts and under representative training load. Compare first-show and later-show frame behavior to baseline. Record actual measurements/observations, including suppression or invisibility, instead of claiming zero stutter. Preserve the local HWND log with the acceptance notes.
5. If any input/focus/rendering regression occurs, stop acceptance, retain reproduction evidence and report it. Do not add native/game hooks or adopt another mechanism without the agreed WPF fallback decision. A failed locked-desktop/input-automation run is an environmental failure, not grounds to modify production focus behavior.
6. Only after training passes should normal **non-ranked** gameplay be considered. Ranked remains a later explicit acceptance gate. No destructive Apex quota/grace expiry test is authorized by this procedure.

PowerShell 7 example, run from the repository after a Release build. The launch is background-only; the actual notice HWNDs become briefly visible. The fifteen-second delay lets the owner return to Apex. This script signals only its unique preview profile and shuts that preview down; it never terminates processes by name.

```powershell
$buildDirectory = Join-Path (Get-Location) 'src\TimeGuard.App\bin\Release\net8.0-windows'
Add-Type -Path (Join-Path $buildDirectory 'TimeGuard.Core.dll')
$previewRoot = Join-Path $env:TEMP ('ScreenTime-preview-' + [Guid]::NewGuid().ToString('N'))
$previewRuntime = [TimeGuard.Services.RuntimeOptions]::Test($previewRoot)
$previewProcess = Start-Process -FilePath (Join-Path $buildDirectory 'TimeGuard.exe') `
    -ArgumentList @('--test-profile', ('"' + $previewRoot + '"'), '--preview-notices') `
    -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
$noticeSignal = [Threading.EventWaitHandle]::OpenExisting($previewRuntime.NoticeEventName)
$stopSignal = [Threading.EventWaitHandle]::OpenExisting($previewRuntime.StopEventName)
try {
    Start-Sleep -Seconds 15
    1..5 | ForEach-Object { [void]$noticeSignal.Set(); Start-Sleep -Seconds 20 }
} finally {
    [void]$stopSignal.Set()
    $noticeSignal.Dispose()
    $stopSignal.Dispose()
    $previewProcess.WaitForExit(5000)
    $previewProcess.Dispose()
}
Write-Host "Local diagnostic evidence: $previewRoot\notice-diagnostics.jsonl"
```

Acceptance record: tested commit/build; UTC/local time; environment listed above; first/subsequent notice kind; foreground before/during/after; minimization/fullscreen state; mouse/keyboard/controller results; visible duration; baseline/notice frametime observations; pass/fail and reproduction notes. Owner Apex acceptance and independent GitHub review remain required before any later merge/release claim.
