# ScreenTime product requirements

This is the current product contract distilled from the approved [architecture](SCREEN_TIME_ARCHITECTURE_PLAN.md), [Phase 1](PHASE1_IMPLEMENTATION.md)–[Phase 4](PHASE4_IMPLEMENTATION.md) records, and the owner's approved remaining-product decisions restated on 2026-09-24. See [DECISIONS.md](DECISIONS.md) for provenance. It does not authorize implementing the next phase.

Phases 1–5 are approved and integrated through `378f9fbf245193089ce55271a10e7c0079a5c882` (PR #1). The [final independent Phase 5 review](https://github.com/dev-kaj223/ScreenTime/pull/1#pullrequestreview-5312587369) clears `476bb76`. Historical reports retain their original validation limits and writing-time status; current source/tests establish actual behavior. Upstream TimeGuard README features are not the ScreenTime contract.

Phase 6 is integrated by normal merge `899ed1344137472bed5dcf1e5e871504b2e719b0` (PR #2). The [final independent Phase 6 review](https://github.com/dev-kaj223/ScreenTime/pull/2#pullrequestreview-5313944356) cleared `4951d1a` with 204 Core tests, 58 UI tests, successful CI and preservation checks, and no additional manual gate. Its fresh native probe cleared the implementation-side foreground-precondition limitation; the historical [Phase 6 report](PHASE6_IMPLEMENTATION.md) remains unchanged.

Phase 7 is integrated by normal merge `cb2d6d08a519cc8fa779e14124d3588f1163cae5` (PR #3). Its [final independent review](https://github.com/dev-kaj223/ScreenTime/pull/3#pullrequestreview-5314429938) cleared `1758006` with 218 Core tests, 64 UI tests, successful CI and preservation checks, a verified real 20-minute original-deadline scenario and five-minute resource soak, and no additional manual gate. The historical [Phase 7 report](PHASE7_IMPLEMENTATION.md) keeps its writing-time validation limits.


Phase 8 is integrated by normal merge `0224f432b65095db4dee59522173ec0267f3e0cb` (PR #4). Its [final independent review](https://github.com/dev-kaj223/ScreenTime/pull/4#pullrequestreview-5318740660) cleared `6b48ac4`, closing H8/P8-G1 with approved B artwork, full author Core 218/UI 69, fresh reviewer Core 218/affected UI 6, CI and preservation evidence. The historical [Phase 8 report](PHASE8_IMPLEMENTATION.md) retains writing-time limits. A subsequent integration-CI timing assertion failure is tracked as [Phase 9 validation follow-up P9-V1](https://github.com/dev-kaj223/ScreenTime/pull/4#issuecomment-5834049344), not erased from prior history.


Phase 9 is integrated by normal merge `2d2b6e6c6eb365a11df99b7094599f3d342a39b1` (PR #5). Its [final independent review](https://github.com/dev-kaj223/ScreenTime/pull/5#pullrequestreview-5319247623) cleared the reviewed package/reproducibility corrections. Historical phase reports retain their writing-time status and limitations.

## Product and safety boundary

- Lightweight, local-only, offline-capable Windows ScreenTime-style app, in the interactive user's session. Evolve the existing .NET/WPF app rather than adding a privileged service or new framework.
- MVP target: Apex Legends process **`r5apex_dx12`**, canonical lowercase without `.exe`. Keep app-keyed models; multi-app coordination is not MVP scope.
- Behavioral enforcement rather than adversarial surveillance. No drivers, injection, game memory inspection, DirectX/graphics hooks, anti-cheat interaction, network filtering, or similar invasive controls. Do not inspect ranked/match state.
- Preserve installed `%AppData%\TimeGuard` data, logs, startup state and live processes. Development uses `%AppData%\ScreenTime-Dev`; tests use unique disposable profiles. No automatic TimeGuard import, fallback into its profile, or development/test autostart registration.
- Enforcement targets a concrete process identity: canonical app name, PID, creation time and Windows session, with current-user ownership revalidated on the retained process handle. Never terminate by name alone, kill a tree/launcher/anti-cheat service, or elevate automatically. Failed termination stays denied, is diagnosed and can be retried.
- Keep work lightweight: roughly five-second ordinary discovery plus known-boundary wakeups, bounded local diagnostics, no busy loops or unrelated-app history. Measure footprint and game performance; do not claim unmeasured budgets or zero stutter.

## Allowance, downtime and accounting

- Selected-app daily allowance and explicit downtime are separate policy facts. Temporary downtime must not become a persisted daily ban. Disabled rules are unrestricted; a zero daily allowance retains the existing unlimited meaning.
- Acceptance configuration example: **Mon–Thu 60 minutes, Fri 90 minutes, Sat–Sun 120 minutes**, with downtime **00:00–08:00 and 08:00–17:00**. These are configurable examples, never policy constants. Adjacent periods deny continuously until 17:00.
- Downtime supports cross-midnight/week boundaries and merged overlap/adjacency. Intervals include their start and exclude their end; next availability also considers quota. Preserve ordinary clock, time-zone and DST handling.
- Account measured awake runtime for configured enabled apps, including background/minimized runtime, once per app even with multiple instances. Use monotonic elapsed time with confirmed instance continuity; do not infer use at first discovery, during sleep, outages or uncertain gaps. Preserve the implemented rebase rules and fractional accounting described in [Phase 3](PHASE3_IMPLEMENTATION.md).
- Persist observed, quota and grace seconds separately; grace seconds are a subset of observed time. Denied runtime can be observed without charging quota. UI visibility, window title, focus and game state are not accounting authorities.
- Local midnight creates a new allowance bucket; it neither bypasses downtime nor resets existing grace. Do not reconstruct unobserved runtime after restart.

## Finish Current Session

- Legitimate quota exhaustion during continuous permitted runtime grants **one persisted 20-minute Finish Current Session episode per app/quota date**. The deadline is the measured exhaustion instant plus 20 minutes, not the later popup or observation time.
- Atomically persist usage, original UTC deadline and captured exact instances before publishing continuation permission. Only instances observed as eligible at the crossing may use grace; the capture set and deadline cannot expand. No uncommitted grant or replacement deadline on persistence failure.
- First discovery with exhausted usage, exhausted startup without an episode, relaunch/replacement/PID reuse, and an allowance decrease that exhausts prior usage do not earn grace. Downtime alone, including a crossing exactly when downtime begins, does not generate grace.
- Existing grace may overlap newly starting downtime and midnight. Only the captured instances continue; new/replacement instances receive no grace. Grace runtime does not consume the next day's quota.
- The persisted UTC deadline survives ScreenTime restart, sleep and midnight. Sleep adds no usage but does not pause the deadline. Restart reconciles exact identities and permits only the original remaining time; overdue resume/restart enforces immediately when execution resumes.
- Confirmed exit/crash of all captured instances completes the episode early; partial exit leaves only surviving captures eligible. Inaccessible observations are not proof of exit. Consumed episode history survives restart and rule recreation, preventing another grant for that quota date.
- Persist expiry before hard termination at the durable deadline, confirm real exit and retry failures without extending time. A carried expired survivor must stop even if a new day's allowance exists. After completion, new launches depend on current downtime/quota; fresh quota never overrides downtime.

## Phase 5: gaming-safe notifications (approved and merged)

The WPF implementation, receipt model, validation evidence and owner's manual procedure are recorded in [Phase 5 implementation](PHASE5_IMPLEMENTATION.md). Following the initial ten-notice result, the owner passed final refined-notification/full-countdown acceptance during active Apex firing-range gameplay on `eca318ed00c677980bb86d3b62a6bb33fd0268c0`; see [the acceptance record](PHASE5_APEX_ACCEPTANCE.md). This is owner-observed acceptance, not a measured performance guarantee. The minimal preview-label and notification-copy follow-ups passed final independent review before PR #1 merged.

The original interactive/topmost TimeGuard warning caused visible Apex disruption. Gaming-safe notifications are non-negotiable; the precise contribution of focus and rendering remains a measurement question.

- Use a minimal WPF notice with `ShowActivated=false`, appropriate Windows no-activate/tool-window behavior (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`), and verified native mouse pass-through. No buttons, keyboard focus, controller/mouse capture, activation calls or game/graphics hooks. Ordinary notices dismiss after approximately 5–8 seconds; the final-minute countdown below is the only approved persistent-display exception.
- Follow the detailed experiment and fallback gate in [architecture section 6](SCREEN_TIME_ARCHITECTURE_PLAN.md#6-notification-architecture). A WPF property alone does not prove pass-through or gaming safety. Consider a native/heavier alternative only if measured results justify it; do not silently relax input/non-interference requirements.
- Notification semantics: approximately 10-minute quota warning, 5-minute quota warning, grace-start/finish-current-session notice, 5-minute grace warning, then hard enforcement at the stored deadline. Display actual remaining grace on recovery; deduplicate/coalesce and discard stale warnings.
- UI stalls, suppressed notices, notification failure and acknowledgement must never determine enforcement or grant/extend grace.
- Center the notice text and shared branding above it; use content-driven height, bounded width/height and wrapping. The replaceable centralized ScreenTime branding asset is shared infrastructure for Phase 8's approved hourglass and later tray/window/taskbar/packaging convergence, not a notification-specific image.
- Default urgency hierarchy: informational ten-minute quota, warning five-minute quota, stronger grace-start, critical five-minute grace, critical final minute, and critical TIME EXPIRED/blocked-expired where applicable. Use short uppercase headings, normal-case explanations and restrained accent/header/icon colors on a neutral background. Communicate state through text as well as color.
- Show a compact passive live countdown during at most the final 60 seconds before the persisted original grace deadline, updating once per second. Consume committed deadline facts only; no per-second database/persistence writes, timing extension, acknowledgement or enforcement ownership. Late delivery/restart uses only the actual remaining portion of that minute; exit, stale state or deadline removes the countdown. Preserve click-through, no activation/input capture and no buttons. Phase 4 still commits expiry before exact-instance hard enforcement.
- Apex validation starts in training/non-ranked gameplay. Record foreground HWND, minimization/focus, mouse/keyboard/controller behavior, dismissal and first/subsequent frame-time effects on the target setup. Ranked validation is a later gate; never provoke a ranked penalty as a test. Helper/automation success alone does not establish Apex acceptance.

## Phase 6: tray/status and protected actions

- Read-only usage/status is available without a password. Include remaining allowance, current restriction/grace state, grace time remaining, next downtime and next availability; present fresh-but-unavailable quota truthfully during downtime.
- D22 approves a collection of configured enabled applications: one or many apps use the same UI, independently accounted/enforced. No aggregate caps, shared grace, cross-app enforcement, overall budgets or passive unrelated-app tracking. Keep a stable shared-brand tray icon, concise one-app or count/restricted-count tooltip, compact per-app rows with read-only detail, bounded scrolling for many apps, and urgency/running/remaining/name sorting. Status countdown uses the original committed deadline only.
- Settings and Exit require the protected password. Gate weakening actions at the command boundary, not merely by hiding UI controls. Preserve password hashing; wrong/cancelled authentication must not change policy or stop monitoring.
- Closing a panel does not stop enforcement. Status consumes policy snapshots; it does not own enforcement state. Expanded history/dashboard or multi-app selection UX is not a prerequisite.
- Add protected notification preferences with Standard / Minimal / Custom presets, per-milestone enable/disable controls, configurable final-countdown visibility/duration, default urgency colors and optional owner-selected Info/Warning/Critical colors with reset-to-default. Countdown duration stays within the approved final-minute maximum. Preferences never weaken enforcement or change quota/grace timing. This is approved Phase 6 scope; its settings UI is not part of Phase 5.
- Standard preserves all existing notices, the 60-second countdown and default colors. Minimal retains grace-start, final countdown and blocked/expired notices; Custom allows each milestone separately. Preview uses example display facts only. Preference storage and UI delivery cannot hold the policy writer lock or rebase accounting. No unconditional Apex rerun is required for Phase 6: independent review determines whether actual rendering changes justify a new game gate; unresolved explicit physical UX gates remain blocking.

## Phase 7: reliability and failure behavior

- Harden lifecycle, persistence integrity, contention handling, shutdown, restart reconciliation, diagnostics and resource cleanup within the existing architecture. Preserve original deadlines, exact identity and commit-before-enforcement.
- Configuration and credential pairs must not be partially published by a failed write or mixed across concurrent reads. Corrupt or unsupported databases must remain intact for diagnosis; never silently reset them or fall back to installed TimeGuard.
- Persistent storage failure retains the supervised nonzero failure path. Bounded SQLite busy waits do not authorize an uncommitted grace grant, an uncommitted expiry kill, a replacement deadline, an automatic watchdog or a new strict fail-safe mode.
- Test realistic faults and lifecycle races in disposable profiles. Report the actual duration, configuration and measurements for resource checks; an accelerated stress test is not evidence of multi-day stability or an unmeasured performance budget.

## Phase 8: user-facing naming and branding

- Use ScreenTime for user-facing naming, titles, executable/product metadata and artifact names. Keep namespaces, project paths, profiles and storage identities stable unless a minimal output-name change is needed for `ScreenTime.exe`.
- Replace the centralized interim mark with the owner-selected hourglass; WPF windows, notices, tray and exported executable/package icons must derive from that shared artwork. The owner explicitly selected **option B — Filled base** on 2026-09-25 (D23), retaining the neutral `#D7DEE9` geometry shown in the comparison.
- Preserve automation IDs, protected commands, passive notification behavior and existing layout. This is naming/branding polish, not a broad UI redesign or Phase 9 distribution/profile change. Keep upstream license attribution.

## Phase 9: distribution preparation

- Build an unsigned self-contained win-x64 portable package reproducibly from clean committed source, with correct ScreenTime metadata/resources and actual bundled license/NOTICE texts. Preserve upstream attribution. See [release procedure](RELEASE.md).
- Distribution-only selection uses `%AppData%\ScreenTime`; ordinary Debug/Release remains Development and explicit Test stays disposable. Never launch/import legacy TimeGuard or fall back to its profile. Autostart is not selected and remains disabled; no watchdog or scheduled task.
- Verify the extracted artifact, its runtime/native/dependency versions, fresh security audit, default selection via a read-only seam, offline-capable local operation, protected commands, exact-owned enforcement, original grace across restart and clean shutdown. Keep real installed/profile preservation evidence and label seeded versus real elapsed timing checks.
- Package CI uploads candidate artifacts only. Signing, public publication, owner credentials, destructive installation/uninstall and real-user migration remain separate gates; complete safe preparation without performing those actions.

## Deferred unless explicitly promoted

Overall multi-app caps; forced breaks; passive unrelated-app tracking; TimeGuard import; deliberate clock-tamper defense; automatic crash watchdog/restart (including Task Scheduler recovery); and broad dashboard/history redesign remain deferred. Retained legacy fields/tests do not authorize reactivating those features. Ordinary restart reconciliation, sleep/wake, midnight and persistent grace are already essential behavior, not deferred recovery features.

## Beta 2: owner UX refinement

D25 authorizes this bounded refinement of the accepted product. First-run password success opens initial configuration once without redundant authentication; later Settings/Exit remain protected and cancelled/wrong authentication is a no-op. Tray left click opens or activates the singleton read-only Dashboard. Tray right click toggles a temporary interactive frameless/no-taskbar status popup near the tray, bounded to the selected monitor work area. Click-away, Escape and repeated tray interaction dismiss it. Its per-app rows directly show truthful remaining/used allowance, downtime end and relative availability, or original grace countdown/deadline with new-session denial; unknown availability stays unknown. Footer: Dashboard, Settings, Exit. Closing views never stops monitoring.

Use h:mm AM/PM for visible schedule, deadline and availability times. Daily-limit hours/minutes convert and round-trip to integer minutes, with 0h0m Unlimited. Downtime supports multi-weekday selection, separate hour/minute/AM-PM controls, explicit next-day periods and clean configured rows. Preserve normalized data, week boundaries, overlap/adjacency, half-open intervals and DST behavior. Remove deferred forced-break/aggregate-cap UI while retaining compatible stored fields.

Dashboard shows Today per configured enabled app (used, remaining, state, next downtime/availability), followed by an intentional empty state or an improved visualization of existing last-seven-day usage. It remains read-only with no analytics subsystem, aggregate policy or passive unrelated-app tracking. Preserve neutral dark surfaces, restrained accents and approved filled-base branding. Reviewer assesses any necessary packaged owner UX/Apex gate after exact-head code review; automation must exercise actual popup activation/dismissal, lifecycle, protected commands, editor save/reload and prior Core/UI regressions.

### Corrected Beta 2 navigation and history (D26)

The current owner correction supersedes the separate Dashboard and Settings windows described above. One main ScreenTime window contains Today and Settings. Today is the password-free default; Settings visibly indicates protection and authenticates with the existing password flow. A successful unlock lasts only while that main window remains open. First-run password creation opens Settings in that window without a second prompt. X closes the UI, clears its Settings unlock, and leaves the tray, monitor and enforcement running. Reopening from the tray opens Today. Left tray click opens or activates this singleton; right tray click toggles only the quick-status popup with zero main-window requests. The popup footer navigates to Today or protected Settings, or invokes independently protected Exit. A new main window opens centered and wholly inside the invoking monitor's work area; page changes and activation of an existing window do not move it. Unsaved Settings changes require Save, Discard or Cancel before leaving.

Today history offers Week (last seven days), Month (last thirty days) and Year (last twelve calendar months including current month). Week and Month use daily buckets; Year aggregates existing DailyUsage into monthly totals. Only configured enabled apps appear independently. Empty ranges explain that no usage has been recorded. Changing range affects only the read-only history view; live Today facts remain current and monitor/policy/usage data remain unchanged. Day/session drilldown remains available where applicable. Use a bounded read-only query and preserve the accepted AM/PM presentation and passive notification path.
