# Agent guidance

Read [project-guide](.agents/skills/project-guide/SKILL.md) before changing this repository. Read the focused skill whose description matches the task; the [skill catalog](docs/agent-skills.md) lists them. Do not run every review for every change.

The repository is in early implementation. [docs/design.md](docs/design.md) owns the product direction; [docs/implementation.md](docs/implementation.md) records implemented scope and limitations. Do not describe roadmap features as implemented.

Use the existing checkout; cloud tasks already run in an isolated environment. Do not create a worktree unless requested. Preserve unrelated changes and inspect Git status before and after work.

When completed work reveals a recurring domain rule, verified procedure, or meaningful pitfall, update the owning skill as part of the task. Prefer a focused update over duplicate instructions. Ground guidance in observed behavior, distinguish proposals from verified procedures, and validate links and skill frontmatter. Exercise executable instructions before describing them as verified.

Report changes, relevant validation, and limitations accurately. Do not claim runtime, security, accessibility, or production readiness from documentation review alone. Commit, push, release, and deployment actions follow the user's authorization; skills do not grant additional authority.
