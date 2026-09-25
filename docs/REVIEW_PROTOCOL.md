# Implementation and independent review

This protocol replaces routine human implementation/review handoffs. Product intent is in [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md), approved choices in [DECISIONS.md](DECISIONS.md), and detailed phase scope in [architecture section 11](SCREEN_TIME_ARCHITECTURE_PLAN.md#11-phased-implementation-plan). Reports are navigation aids, never proof. No API dependency or custom orchestrator is required.

## Owner-authorized Phases 6–9 delivery

The owner's 2026-09-25 objective authorizes routine implementation, validation, commits/pushes, PRs, independent review, remediation/re-review, CI verification, and normal merge commits into `screentime-dev`, continuing to the next phase without human relay. This supersedes the historical setup's no-push-before-approval text for the current goal. Each mutable implementation uses an isolated phase worktree; a separate reviewer context inspects a worktree pinned to the exact remote head and posts directly to GitHub. The implementation agent retrieves findings from GitHub. Preserve `master`, never push `upstream`, and do not rewrite or squash approved history.

All ten merge gates must pass: current phase only; required local build/tests; GitHub CI; independent actual-diff/source/test review; zero unresolved blockers; every actionable correction re-reviewed on the exact head; no open required manual gate; installed TimeGuard preservation; target only `screentime-dev`; normal merge commit preserving history. Reverify exact head/base and fetch the resulting integration SHA after merge.

Escalate only for genuine product conflicts/unspecified important behavior; explicitly required physical UX/gaming/display/installation/hardware gates that automation cannot establish; destructive/security-sensitive real-machine actions; credentials/signing/secrets/release publication/store authorization; material unresolved architectural disagreement; unreliable required validation after reasonable diagnosis/retries; material scope expansion; or subjective visual choices without an approved answer. Complete safe independent work before stopping at the narrow gate. Routine phase completion, PR readiness, green CI, remediation, or merge readiness are not approval requests.

Phase 6 does not automatically require rerunning accepted Apex gameplay checks. Independent review must assess actual rendering changes and identify any justified new manual game gate. Automated tray/password/status and native passive-notification regressions remain required; automation cannot claim unperformed hardware/game evidence. Historical reports remain unedited.

## Roles and independence

**Implementer:** implement only the requested phase, add meaningful tests, validate, commit/push a phase branch, open a PR into `screentime-dev`, obtain independent review, retrieve findings directly from GitHub, remediate and request re-review until no material findings remain. Then validate the final revision and merge under the gates below.

**Reviewer:** use repository Codex Code Review or a separate Codex reviewer context/worktree. Do not self-certify work from the implementation conversation. Inspect the actual base-to-head diff, relevant surrounding source, tests and specification. Do not accept reports, passing counts, screenshots or PR prose as substitutes for inspection. The initial review pass must not modify production code; report defects and suggested verification instead. Keep fixes in the implementer's worktree. Re-review corrections and their possible regressions.

GitHub PR comments/reviews are the shared record. Identify each pass by **base SHA and head SHA**, scope inspected, validation actually performed, and limitations. An empty response, pending review, stale approval or lack of bot findings does not prove that this protocol was completed.

## Implement → review → merge

1. **Start:** read the root map, requirements, decisions, phase plan and relevant prior-phase reports/source/tests. Check the worktree is clean and preserve unrelated changes. Fetch `origin`, fast-forward local `screentime-dev`, and create `codex/phase-N-<topic>` from it. Never start from upstream `master` or rewrite completed phase history.
2. **Implement:** keep the phase boundary explicit. Use isolated profiles and owned helpers. Add tests for observable behavior and failures; do not weaken old assertions to hide regressions. Update requirements/decisions only for an approved decision; keep implementation detail in the phase report/PR.
3. **Validate and publish:** run applicable gates below, inspect the complete diff and commit only intended files. Push the phase branch to `origin`; open a ready-for-review PR with explicit base `screentime-dev` (the repository default is `master`). Include scope, requirement/test mapping, migration impact, exact commands/results, limitations and head SHA. Attach the PR to the Codex task when supported. Never publish profile data, credentials or private diagnostics.
4. **Independent review:** if repository Code Review is enabled, invoke it as below. Otherwise leave the PR ready for the dedicated reviewer and dispatch that reviewer with the PR URL, phase and SHAs. Use the fallback also when a bot pass cannot cover the full protocol. Do not wait for the owner to copy findings between agents.
5. **Retrieve and remediate:** fetch GitHub review submissions, inline review threads and conversation comments, including all pages. Track each actionable finding by link/ID, severity and disposition. Fix valid findings, add appropriate regression coverage and explain the correction on GitHub. For an inapplicable finding, supply concrete evidence and obtain reviewer agreement; the implementer cannot declare their own disputed blocker resolved.
6. **Re-review:** commit and push corrections to the same phase branch without force-pushing. Request a new independent pass against the new head SHA. Repeat until no material unresolved findings remain. Non-blocking follow-ups require an explicit reason and reviewer agreement; they cannot disguise missing product acceptance.
7. **Final validation:** after the last correction, confirm GitHub head/base SHAs, review conclusions, unresolved threads, CI and local/manual evidence. Fetch the latest target; if it changed, merge `origin/screentime-dev` into the phase branch, resolve conflicts, rerun affected validation, push and re-review. Never certify new content with an older reviewed SHA.
8. **Merge:** only after all applicable required tests pass, acceptance evidence is present, and independent review is clear for the final head/base. Honor GitHub protections and required approvals; never use an admin bypass. Merge into `screentime-dev` using an allowed method (prefer a merge commit to preserve phase commits). Confirm GitHub reports the PR merged and refresh the local target. Never merge into `master` or `upstream`.

After one-time setup approval and an authorized phase task, routine commits, pushes, PRs, review comments, corrections, re-review and gated merge do not require another human handoff. Explicit task restrictions, including this setup's **no push before approval**, take precedence.

### Command skeleton (PowerShell)

Replace placeholders; check exit codes and stop dependent steps on failure. These are instructions for future authorized phases, not commands run by this setup.

```powershell
gh --version
gh auth status
git remote -v
git status --short --branch
git fetch origin
git switch screentime-dev
git merge --ff-only origin/screentime-dev
git switch -c codex/phase-N-topic
# Implement and validate; stage explicit intended paths, then:
git commit -m "phaseN: describe the completed change"
git push -u origin codex/phase-N-topic
gh pr create --repo dev-kaj223/ScreenTime --base screentime-dev --head codex/phase-N-topic --title "Phase N: description" --body-file <pr-body-file>
```

Use a UTF-8 temporary file for multiline PR/review text; never interpolate it as shell code. The existing authenticated GitHub connector can perform equivalent PR/review operations if CLI access is unavailable. Its session does not authenticate `gh` or prove Git push access. Do not extract connector credentials or alter credential helpers. Diagnose access failures; request approval if authentication/permission changes are required.

## Reviewer checklist

For each area, compare **specification vs implementation** and independently **specification vs tests**. Trace requirements into production paths and meaningful assertions, not just test names. Review unchanged dependencies when needed.

- **Scope:** requested phase only; no missing acceptance criteria, invented product policy, deferred features or unrelated cleanup.
- **All prior phases:** isolation/startup/process ownership and lifecycle (1); pure decisions, serialized coordinator and UI-independent enforcement (2); measured awake usage, downtime, midnight, DST/time-zone and restart accounting (3); atomic grace, exact capture, relaunch denial, fixed deadline, exit/expiry, sleep/midnight/downtime overlap and next-day quota (4). Extend this list with each completed later phase.
- **Process safety:** PID reuse, replacement, user/session mismatch, inaccessible/vanished targets, retained handles, confirmed termination and failed-kill retry. No name-wide/tree termination, invasive game access, automatic elevation or collateral helper/process cleanup.
- **Persistence/migration:** consistent backup, transaction/rollback, idempotence, unknown future schema, corrupt/unavailable storage, canonical-key collisions, immutable deadlines/captures, crash between commit and external action, restart reconciliation and installed-TimeGuard rejection. Never delete/recreate a database to hide failure.
- **UI/game performance:** enforcement cannot await the dispatcher, successful notification or acknowledgement. Check focus/input capture, stale/duplicate notices, bounded dismissal/queueing, timers/resources and polling/IO footprint. Require actual Apex evidence for notification acceptance; automation cannot prove game non-interference.
- **Tests:** missing negative/boundary/regression cases, vacuous assertions, changed/removed tests, profile isolation/ownership, deterministic clock seams and reliable integration/UI evidence. Distinguish exercised production paths from mocks and manual claims. Run relevant checks independently where possible; state what the review environment cannot execute.

Post each finding with: stable ID, **blocking/non-blocking**, optional P0–P3 priority, violated requirement, concrete failure scenario/impact, file and current diff line (or source line/commit when outside the diff), and expected correction/test. Blocking means material correctness, safety, persistence, acceptance, scope or required-validation failure; it is not limited to a bot's priority filter. Non-blocking means optional improvement without a material contract violation. Report a clear outcome even with no findings, including examined SHAs and limitations.

## Repository Codex Code Review

Enable Code Review for this repository in [Codex settings](https://chatgpt.com/codex/settings/code-review), then post **`@codex review`** as a PR conversation comment. Codex reads applicable `AGENTS.md` rules. Automatic review on PR opening is optional; explicitly request a fresh pass after corrections. See [official OpenAI documentation](https://learn.chatgpt.com/docs/third-party/github).

```powershell
gh pr comment <PR> --repo dev-kaj223/ScreenTime --body "@codex review"
```

Keep review separate from remediation: do not ask the reviewer bot to push fixes. Wait for completion; an eyes reaction only acknowledges work. Verify the reviewed commit. The current documented GitHub bot focuses on P0/P1 findings, so a clean bot response alone does not establish coverage of material lower-priority issues, missing tests or every checklist item. Obtain a dedicated reviewer pass whenever full protocol coverage is not established.

Retrieve all three GitHub channels and review-thread resolution state after each pass:

```powershell
gh pr view <PR> --repo dev-kaj223/ScreenTime --json url,baseRefName,baseRefOid,headRefOid,reviewDecision,mergeStateStatus,statusCheckRollup
gh api --paginate repos/dev-kaj223/ScreenTime/pulls/<PR>/reviews
gh api --paginate repos/dev-kaj223/ScreenTime/pulls/<PR>/comments
gh api --paginate repos/dev-kaj223/ScreenTime/issues/<PR>/comments
gh pr checks <PR> --repo dev-kaj223/ScreenTime
```

Use GitHub's review-thread view or the connector's `list_pull_request_review_threads` in addition to those REST lists; paginate any partial response. Fetch replies and reconcile resolved threads with reviewer conclusions. Resolved UI state does not prove a correction exists. If reviews/comments disagree, obtain reviewer clarification on GitHub.

## Fallback: two worktrees, separate reviewer context

The implementer retains the phase branch in worktree A. Prepare worktree B at the **remote PR head SHA**, detached, with a separate Codex reviewer task/context. Reuse an established reviewer task; if none exists, establish it once during setup rather than requiring the owner to relay every review. Supply the PR URL, approved phase, requirements/protocol links and exact base/head SHAs. The reviewer derives their own conclusions from the repository.

```powershell
git fetch origin
git fetch origin pull/<PR>/head
git rev-parse FETCH_HEAD
# Verify this SHA equals GitHub's headRefOid before creating the worktree.
git worktree add --detach <review-worktree-path> <verified-head-SHA>
git -C <review-worktree-path> diff <verified-base-SHA>...HEAD --stat
git -C <review-worktree-path> diff <verified-base-SHA>...HEAD
```

Use a separate permitted directory; never overwrite an existing worktree or its changes. Obtain required filesystem permission rather than weakening isolation. Fetch missing base commits as needed. The reviewer reads source/tests and may run isolated validation but does not edit production code during the initial pass. Capture results outside tracked source. Post findings to the actual GitHub PR using inline comments where possible and a summary review/comment with SHAs, evidence and verdict. A reviewer sharing the author's GitHub identity may need a `COMMENT` verdict instead of GitHub `APPROVE`; this does not satisfy a protection rule demanding another account's approval.

After remediation, verify worktree B is clean, fetch and detach it at the new verified head SHA, and review the full updated PR plus fixes. Never hard-reset away work. The implementer retrieves findings from GitHub itself. The reviewer posts an explicit final **clear** or **blocking** outcome for the current revision. No chat-only or locally saved review substitutes for the GitHub record.

## Validation and merge gates

For production phases, use Windows with .NET 8 and an unlocked interactive desktop for FlaUI. Run from the relevant worktree, serializing desktop UI tests:

```powershell
dotnet restore TimeGuard.sln
dotnet build TimeGuard.sln --no-restore --no-incremental -c Release
dotnet test tests/TimeGuard.Tests/TimeGuard.Tests.csproj --no-build -c Release --logger trx
dotnet test tests/TimeGuard.UITests/TimeGuard.UITests.csproj --no-build -c Release --logger trx
git diff --check
```

- Run prior-phase regressions and the requested phase's acceptance checks; add focused checks for changed behavior. Preserve the known `DashboardWindow.xaml.cs` CS0618 warning as baseline debt; investigate new warnings.
- Real-process checks use fixture-owned `ScreenTime.TestProcess` only. For grace/deadline changes, include the actual-duration helper gate in [Phase 4](PHASE4_IMPLEMENTATION.md): set `SCREENTIME_PHASE4_FULL_DURATION=1` only in that runner's environment, run `Phase4HelperTests.OwnedHelper_Grant_RelaunchDenied_RestartPreservesDeadline_Expires`, and restore the previous environment value afterward. Short deterministic tests remain necessary.
- Follow phase manual gates. Phase 5 requires measured non-ranked Apex notification validation; Phase 6 rechecks tray/password/status and prior MVP behavior. Keep actual evidence of hardware sleep/resume or real-calendar tests when claimed; simulated clocks are not hardware evidence.
- For runtime/integration validation, verify installed TimeGuard files and HKCU Run state with read-only before/after checks. Do not launch the legacy-production profile. Keep profiles/logs/TRX local or sanitized; report commands, configuration, SHAs, counts and limitations on GitHub.
- The [ScreenTime validation workflow](../.github/workflows/screentime-validation.yml) builds and runs Core tests on `screentime-dev` PRs. It does not certify interactive UI or Apex acceptance. Legacy packaging targets `master`; a PR with no checks is not a pass. Never bypass failed/missing required checks or required reviewers.
- Documentation-only changes require link/consistency/scope and whitespace validation. Build/test when changing validation configuration or executable behavior; prose alone does not require Apex or a 20-minute test.
- Before merging, every material finding must be closed by evidence and reviewer agreement, applicable tests/manual gates must pass on the final revision, and GitHub protections must be satisfied. `gh pr merge <PR> --repo dev-kaj223/ScreenTime --merge --match-head-commit <reviewed-head-SHA>` guards against a changed head; choose an allowed non-rewriting method if merge commits are unavailable. Never pass `--admin`. A changed base requires step 7 reconciliation.

## Human escalation boundary

Stop only when product requirements conflict or are genuinely unspecified; a destructive/security-sensitive action requires approval; implementer and reviewer cannot resolve an architectural disagreement; required tests remain unreliable after reasonable diagnosis; or completing the work would materially expand phase scope. Provide evidence, attempted diagnostics and the narrow decision needed. Routine bugs, review corrections, test additions and phase-local design choices are agent work.

Exceptional infrastructure failures (credentials, permissions, unavailable interactive desktop or reviewer) should first receive bounded diagnosis and the documented fallback. Never silently waive a gate, change credentials/protections, fabricate a clean review or loop indefinitely. Request only the setup/access decision needed when autonomous recovery is exhausted. Do not claim unperformed manual acceptance tests.

## Setup evidence and one-time actions

Read-only investigation on **2026-09-24**:

| Check | Observed result |
|---|---|
| `gh --version`, `gh auth status` | Both fail: `gh` is not recognized. Standard Program Files, per-user Programs and WinGet-link locations also lack it. CLI authentication cannot be assessed; no credentials changed. |
| `git remote -v` | `origin`: `https://github.com/dev-kaj223/ScreenTime.git`; `upstream`: `https://github.com/debmis/TimeGuard.git` (fetch/push). Only origin is a delivery target. |
| Local branch/history | Clean `screentime-dev` tracking `origin/screentime-dev` at inspection; Phase 4 head `3b1dbec`. Setup changes remain local. |
| Connected GitHub access | Connector can read ScreenTime; owner account `dev-kaj223` has push/admin access and the connected app installation covers all repositories. This does not establish CLI credentials or Code Review enablement. |
| Codex Code Review | **Unverified, not confirmed available or disabled.** No PRs were returned to inspect for past bot reviews. Code Review settings redirected to logged-out ChatGPT in the available browser; no repository toggle was observable. Use fallback until enablement is verified. |
| Existing governance | Default branch is `master`; existing build workflow targets `master`; `.github/CODEOWNERS` names upstream `@debmis`. Remote protections/rulesets were not verified. No remote settings changed. |

Local setup validation: restore and a full Release build passed (zero errors; the existing CS0618 warning only), and **174 Core tests passed, zero failed/skipped**. Documentation link/anchor and whitespace checks passed. The initial restore was blocked by sandbox access to the existing NuGet configuration; an approved retry succeeded without changing it. The workflow's commands were exercised locally and its structure inspected; GitHub-hosted execution remains unverified until publication. Interactive UI, Apex and full-duration grace tests were not rerun for this documentation/CI-only change. No application source, tests or historical phase documents changed.

One-time completion steps:

1. Owner approves this setup before any push. Publish/review setup separately; this does not authorize Phase 5 or merging old phase branches.
2. If CLI is the chosen transport, install GitHub CLI and authenticate with the intended account, then rerun both checks and verify repository access. Do not alter authentication/credentials without required authorization. The already-authorized connector can handle PR/review operations while Git push uses its own authorized transport.
3. In authenticated Codex settings, verify/enable repository Code Review if desired. Until confirmed, establish one dedicated reviewer worktree/task with GitHub posting access. After the first approved PR exists, invoke review and verify its response; do not create an empty PR merely to test access.
4. Inspect `screentime-dev` protections/rulesets and the inherited `@debmis` CODEOWNERS requirement. Obtain owner approval for any required ownership/protection change; do not request upstream approval as a routine handoff or bypass a required approval. Once the new workflow runs, configure its `Build and Core tests` check as required if enforcing this gate server-side. A same-account reviewer comment cannot satisfy a different-account approval rule.

This is an agent operating procedure, not an automatic orchestration service. CI and GitHub protections enforce only configured checks; reviewer and implementer remain responsible for the full acceptance/review record.
