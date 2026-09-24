# ScreenTime agent map

ScreenTime is a lightweight Windows, selected-app screen-time utility. Keep the existing C#/.NET 8, WPF, Core/App and SQLite boundaries; internal `TimeGuard.*` names are intentional.

## Read before working

- [Product requirements](docs/PRODUCT_REQUIREMENTS.md): current invariants, remaining MVP requirements and deferred scope.
- [Decisions](docs/DECISIONS.md): approved choices, provenance and how to handle conflicts.
- [Review protocol](docs/REVIEW_PROTOCOL.md): branch/PR/review/remediation/validation/merge procedure, commands and setup status.
- [Architecture plan](docs/SCREEN_TIME_ARCHITECTURE_PLAN.md): detailed design and phase boundaries, especially sections 6–11. Its original-code diagnosis is historical.
- [Phase 1](docs/PHASE1_IMPLEMENTATION.md), [Phase 2](docs/PHASE2_IMPLEMENTATION.md), [Phase 3](docs/PHASE3_IMPLEMENTATION.md), [Phase 4](docs/PHASE4_IMPLEMENTATION.md): historical implementation evidence and test limitations. Verify actual source/tests; reports are not proof.

## Working rules

- Implement only the requested phase on `codex/phase-N-<topic>` from `screentime-dev`. Follow the review protocol through independent review, remediation and final validation before merging back. Do not rewrite prior phase history or push to `upstream`.
- Preserve installed TimeGuard data, startup registration and live processes. Use isolated development/test profiles and fixture-owned `ScreenTime.TestProcess` helpers. Never use name-wide/process-tree kills or real games as destructive test fixtures.
- Core owns policy, measured accounting, persistence and exact-instance enforcement. UI/notification delivery must never grant time, own a deadline or delay enforcement. No invasive game/graphics/anti-cheat integration.
- Keep requirements and decisions current when an approved change affects them; link to historical detail instead of copying reports. Do not promote deferred features or treat a candidate architecture file list as a mandate.
- Continue routine implementation/review work autonomously. Escalate only the product, scope, security/destruction, unresolved architecture or unreliable-validation exceptions in the review protocol.

## Code Review Rules

- Follow [REVIEW_PROTOCOL.md](docs/REVIEW_PROTOCOL.md) independently against the actual PR diff, source and tests. Initial review is read-only for production code; classify findings with concrete evidence.
- Block regressions in exact-instance/ownership enforcement, isolated profiles, atomic durable grace, quota/downtime separation, or gaming-safe UI independence. Check prior-phase regressions and missing tests even when the implementer's report says validation passed.
