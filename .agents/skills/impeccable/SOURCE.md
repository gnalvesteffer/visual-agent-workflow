# Impeccable source and local integration

- Upstream project: [pbakaus/impeccable](https://github.com/pbakaus/impeccable)
- Pinned upstream commit: `414603bf14c7a344edb466bb1f85690c77ce8b7d`
- Upstream skill version: 4.8.0
- Upstream engine installed by the project updater: 0.1.18 (`linux-x64` in this environment)
- License: Apache-2.0; see [LICENSE](LICENSE), copied from the upstream repository root at the pinned commit.
- Vendored from the upstream `.agents/skills/impeccable` directory, preserving references and helper scripts.

Impeccable is the general-purpose UI design skill for this repository. It complements the project-specific [graph-experience-review](../graph-experience-review/SKILL.md), repository [AGENTS.md](../../../AGENTS.md), and [editor design system](../../../docs/design-system.md). Follow repository architecture, accessibility, embedding, and testing requirements when applying it.

The project-scoped updater was run with `impeccable update --project --yes`; it refreshed the vendored skill and installed the Impeccable engine and Codex post-edit/deep-review hook manifest at `.codex/hooks.json`. The user may need to approve the Codex hook through `/hooks` on first use. Hook settings currently use defaults because `.impeccable/config.json` is absent. `impeccable hooks status` reports the project hook as enabled.
