# Agent guidance

Read [project-guide](.agents/skills/project-guide/SKILL.md) before changing this repository. Read the focused skill whose description matches the task; the [skill catalog](docs/agent-skills.md) lists them. Do not run every review for every change.

The repository is in early implementation. [docs/design.md](docs/design.md) owns the product direction; [docs/roadmap.md](docs/roadmap.md) tracks priorities, implemented scope, and limitations. Do not describe roadmap features as implemented.

Use the existing checkout; cloud tasks already run in an isolated environment. Do not create a worktree unless requested. Preserve unrelated changes and inspect Git status before and after work.

When completed work reveals a recurring domain rule, verified procedure, or meaningful pitfall, update the owning skill as part of the task. Prefer a focused update over duplicate instructions. Ground guidance in observed behavior, distinguish proposals from verified procedures, and validate links and skill frontmatter. Exercise executable instructions before describing them as verified.

When fixing an identified defect or regression, add or update a focused automated regression test that would fail before the fix and passes afterward. Prefer the narrowest test that exercises the user-visible or contract-level failure; do not merely duplicate the implementation. If a regression cannot be tested meaningfully or automatically, record why and add the strongest practical verification available.

Report changes, relevant validation, and limitations accurately. Do not claim runtime, security, accessibility, or production readiness from documentation review alone. Commit, push, release, and deployment actions follow the user's authorization; skills do not grant additional authority.

For changes to behavior, architecture, APIs, setup, or UI tasks, use [documentation-maintenance](.agents/skills/documentation-maintenance/SKILL.md). Update the affected architecture and user-guide sections alongside implementation, keep shipped behavior distinct from future design, and validate useful Mermaid diagrams and local links.
