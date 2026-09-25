# Phase 5 owner acceptance record

**Status: final owner refined-presentation/countdown Apex firing-range acceptance PASS on `eca318ed00c677980bb86d3b62a6bb33fd0268c0`.** Only the requested preview-label/documentation follow-up awaits final independent review. Keep PR #1 unmerged. Use the notice-only preview and commands in [Phase 5 implementation](PHASE5_IMPLEMENTATION.md#owners-manual-apex-acceptance-procedure); it starts no monitor or enforcement. No destructive Apex enforcement or ranked testing is authorized.

## Final owner acceptance on reviewed head `eca318e`

The owner reports that refined notifications and the **full final-minute live countdown** passed during **active Apex firing-range gameplay** on `eca318ed00c677980bb86d3b62a6bb33fd0268c0`:

- Gameplay performance remained **10/10** in the owner's assessment.
- No perceived FPS drop or rendering hitch.
- No focus loss or controller/gameplay interruption.
- Countdown updated correctly once per second.
- Urgency colors were clear and appropriately subtle.
- Dynamic sizing and centered presentation looked good.

This completes the owner's refined-presentation/countdown firing-range acceptance. These are owner observations, not instrumented FPS/frametime measurements or claims about additional display configurations. The exact tested head is supplied; hardware details, raw HWND logs and per-notice measurements were not supplied. No non-ranked match, ranked run or destructive enforcement is inferred.

The sole requested follow-up replaces the preview's synthetic `DisplayName` of `ScreenTime preview` with **`Example App`**, avoiding repetition beneath the brand/logo. Production bodies continue to identify the affected application through `DisplayName` (for example, Apex Legends). Behavior, styling, native input/focus handling, countdown timing and enforcement separation are unchanged. Owner gameplay acceptance above applies to the explicitly tested head; the minimal follow-up receives focused automated validation and final independent review.

## Owner observations received 2026-09-24

The owner exercised the then-current passive WPF implementation through **two complete five-notice cycles, ten notices total**, during **active Apex firing-range gameplay**, before the requested presentation/countdown refinements.

- All notices appeared and auto-dismissed.
- No perceived FPS degradation or appearance/disappearance hitch.
- No focus loss or minimization/fullscreen transition.
- No controller or gameplay interruption.
- Repeated notices caused no observed cumulative degradation.

The owner accepted WPF-first based on these earlier gameplay/non-interference observations. This was an owner-reported functional result, not instrumented frametime/FPS proof. That initial report did not supply exact tested binary hash, GPU/display configuration, raw HWND samples, precise durations, mouse pass-through measurements or a non-ranked match run. The new final-minute countdown was not part of those ten notices. Visual acceptance was still pending at that stage; the final result above supersedes that pending status.

The optional detail fields below remain unfilled where the owner supplied an aggregate result rather than individual measurements. They do not change the reported final PASS into a NOT RUN status.

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
| Refined notices (aggregate owner report) | Active firing-range gameplay; per-kind/display-mode detail not supplied | No focus loss reported; raw HWNDs not supplied | Not separately reported in final update | No controller/gameplay interruption | Not supplied | No perceived FPS drop/hitch; owner performance rating 10/10 | PASS; colors, centering and dynamic sizing accepted |
| Full final-minute countdown | Active firing-range gameplay | No focus loss reported; raw HWNDs not supplied | Not separately reported in final update | No controller/gameplay interruption | Full final minute reported; precise measurement not supplied | No perceived FPS drop/hitch | PASS; correct once-per-second updates |

Any reproducible focus, input, fullscreen or rendering regression stops acceptance; preserve evidence and request the WPF fallback decision. Suppression/invisibility must be recorded. Do not modify production activation behavior based on a failed secure-desktop or foreground-acquisition test precondition.

## Later gates

- Previous five-notice presentation, owner functional firing-range result: **PASS as observed above**
- Refined presentation/countdown, owner visual and final firing-range sign-off: **PASS on `eca318e`**
- Final independent review of acceptance evidence and minimal preview-label follow-up: **PENDING**
- Normal non-ranked testing, only after training passes: **NOT RUN**
- Ranked: **later explicit gate; NOT RUN**
