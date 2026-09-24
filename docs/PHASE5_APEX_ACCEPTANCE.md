# Phase 5 owner acceptance record

**Status: NOT RUN — human/manual gate remains open.** Automated helper results do not establish Apex safety. Keep PR #1 unmerged until this evidence is completed and reviewed. Use the notice-only preview and commands in [Phase 5 implementation](PHASE5_IMPLEMENTATION.md#owners-manual-apex-acceptance-procedure); it starts no monitor or enforcement. No destructive Apex enforcement or ranked testing is authorized.

## Environment to record

- Owner/date/time:
- Tested commit and Release build:
- Windows build; GPU/driver:
- Display(s), resolution, refresh, scaling; active monitor:
- Apex mode (borderless/exclusive where supported):
- Input devices and controller connection:
- Built-in performance display; baseline idle/load observations:
- Local diagnostic path and preserved HWND samples:
- Existing enforcers accounted for without modifying installed TimeGuard:

## Firing range / training only

Run first and subsequent sequences under idle and representative training load. Repeat for each supported display mode/layout. Record observations or measurements rather than assuming PASS. Check several opaque/text locations for mouse pass-through and held-key/controller continuity. Record first-show versus later-show frametimes relative to baseline using game-provided information, without injected tools.

| Notice | First/subsequent; idle/load; display mode | Foreground HWND before/during/after | Minimize/fullscreen unchanged | Mouse/held-key/controller continuity | Visible seconds | Baseline vs notice frametime observations | Pass/fail and notes |
|---|---|---|---|---|---|---|---|
| Ten-minute quota preview | NOT RUN | | | | | | |
| Five-minute quota preview | NOT RUN | | | | | | |
| Grace-start preview | NOT RUN | | | | | | |
| Final-grace preview | NOT RUN | | | | | | |
| Blocked preview | NOT RUN | | | | | | |

Any reproducible focus, input, fullscreen or rendering regression stops acceptance; preserve evidence and request the WPF fallback decision. Suppression/invisibility must be recorded. Do not modify production activation behavior based on a failed secure-desktop or foreground-acquisition test precondition.

## Later gates

- Training result and owner sign-off: **PENDING**
- Independent review of measured target-setup evidence: **PENDING**
- Normal non-ranked testing, only after training passes: **NOT RUN**
- Ranked: **later explicit gate; NOT RUN**
