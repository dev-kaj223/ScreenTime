# ScreenTime repository guidance

Read the root [AGENTS.md](../AGENTS.md) and follow its linked product requirements, decisions and review protocol. They are the shared agent instructions for this repository.

README describes the current ScreenTime development build. Historical implementation reports retain their writing-time evidence; they are not substitutes for current source or validation. Enforcement belongs in Core, not WPF; selected-app downtime and quota are separate, and development/tests must preserve installed TimeGuard state.

Use ScreenTime for user-facing names and the shared branding resource for icons. Internal TimeGuard namespaces/project paths remain intentional. Branding work must not alter profiles, startup registration, policy or automation IDs; release construction and production-profile selection belong to the separately scoped packaging phase. Preserve the upstream MIT attribution in LICENSE.
