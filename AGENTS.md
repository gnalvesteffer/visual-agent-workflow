# Agent guidance

Read [project-guide](.agents/skills/project-guide/SKILL.md) before changing this repository. Read the focused skill whose description matches the task; the [skill catalog](docs/agent-skills.md) lists them. Do not run every review for every change.

The repository is in early implementation. [docs/design.md](docs/design.md) owns the product direction; [docs/roadmap.md](docs/roadmap.md) tracks priorities, implemented scope, and limitations. Do not describe roadmap features as implemented.

Use the existing checkout; cloud tasks already run in an isolated environment. Do not create a worktree unless requested. Preserve unrelated changes and inspect Git status before and after work.

When completed work reveals a recurring domain rule, verified procedure, or meaningful pitfall, update the owning skill as part of the task. Also consider whether the task exposed a reusable skill gap that would help future contributors; add a focused repository skill when it has clear, repeated value, and avoid duplicates or one-off checklists.

For general-purpose skill gaps, look for relevant skills online before writing from scratch. Prefer credible, maintained sources with clear provenance and reuse terms. Read the source, adapt only the useful parts to this repository's architecture and governing instructions, and preserve attribution/license requirements for copied or vendored material. Do not adopt conflicting, overly broad, unsafe, or tool-specific instructions verbatim. If no suitable source exists, author concise project-specific guidance and say so in its provenance notes where useful.

When adding or renaming a skill, update [the skill catalog](docs/agent-skills.md). Keep frontmatter valid, link the source and license when applicable, ground guidance in observed behavior, distinguish proposals from verified procedures, and validate links. Exercise executable instructions before describing them as verified.

When fixing an identified defect or regression, add or update a focused automated regression test that would fail before the fix and passes afterward. Prefer the narrowest test that exercises the user-visible or contract-level failure; do not merely duplicate the implementation. If a regression cannot be tested meaningfully or automatically, record why and add the strongest practical verification available.

Report changes, relevant validation, and limitations accurately. Do not claim runtime, security, accessibility, or production readiness from documentation review alone. Commit, push, release, and deployment actions follow the user's authorization; skills do not grant additional authority.

For changes to behavior, architecture, APIs, setup, or UI tasks, use [documentation-maintenance](.agents/skills/documentation-maintenance/SKILL.md). Update the affected architecture and user-guide sections alongside implementation, keep shipped behavior distinct from future design, and validate useful Mermaid diagrams and local links.
