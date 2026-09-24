# Phase 5 owner acceptance record

**Status: owner-observed functional non-interference PASS for the previous presentation; refined visuals/countdown and final validation PENDING.** Keep PR #1 unmerged. Automated helper results do not substitute for final owner visual/gameplay acceptance. Use the notice-only preview and commands in [Phase 5 implementation](PHASE5_IMPLEMENTATION.md#owners-manual-apex-acceptance-procedure); it starts no monitor or enforcement. No destructive Apex enforcement or ranked testing is authorized.

## Owner observations received 2026-09-24

The owner exercised the then-current passive WPF implementation through **two complete five-notice cycles, ten notices total**, during **active Apex firing-range gameplay**, before the requested presentation/countdown refinements.

- All notices appeared and auto-dismissed.
- No perceived FPS degradation or appearance/disappearance hitch.
- No focus loss or minimization/fullscreen transition.
- No controller or gameplay interruption.
- Repeated notices caused no observed cumulative degradation.

The owner accepts WPF-first based on these gameplay/non-interference observations. This is an owner-reported functional result, not instrumented frametime/FPS proof. The report did not supply exact tested binary hash, GPU/display configuration, raw HWND samples, precise durations, mouse pass-through measurements or a non-ranked match run. No such evidence is inferred. The new final-minute countdown was not part of those ten notices. Visual acceptance explicitly remains incomplete until the refinements and final validation are reviewed.

The fields and table below are for **the refined presentation and six-state cycle**, not a denial or relabeling of the above completed functional observation.

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
| Final-minute countdown (at most 60 seconds, once per second) | NOT RUN | | | | | | |

Any reproducible focus, input, fullscreen or rendering regression stops acceptance; preserve evidence and request the WPF fallback decision. Suppression/invisibility must be recorded. Do not modify production activation behavior based on a failed secure-desktop or foreground-acquisition test precondition.

## Later gates

- Previous five-notice presentation, owner functional firing-range result: **PASS as observed above**
- Refined presentation/countdown, owner visual and final firing-range sign-off: **PENDING**
- Independent review of measured target-setup evidence: **PENDING**
- Normal non-ranked testing, only after training passes: **NOT RUN**
- Ranked: **later explicit gate; NOT RUN**
