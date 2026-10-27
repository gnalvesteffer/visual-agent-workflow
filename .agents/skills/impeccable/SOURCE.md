# Impeccable source and local integration

- Upstream project: [pbakaus/impeccable](https://github.com/pbakaus/impeccable)
- Pinned source commit: `989c484c091d26f10fa102767eefc0da2e62ec67`
- Upstream skill version: 4.7.1
- License: Apache-2.0; see [LICENSE](LICENSE)
- Installed from the upstream `.agents/skills/impeccable` directory, preserving its references, scripts, metadata, and agent support files.

This repository vendors Impeccable as its primary general-purpose UI design skill. It complements, rather than replaces, the project-specific [graph-experience-review](../graph-experience-review/SKILL.md) and [editor design system](../../../docs/design-system.md). Follow repository architecture, accessibility, embedding, and testing requirements when applying it.

The upstream installer and detector hooks were not run. This skill is already in the repository's project-scoped `.agents/skills` location; hooks are opt-in and were intentionally left disabled. Do not run `/impeccable init` or other commands that write project design artifacts unless the task calls for those artifacts.
