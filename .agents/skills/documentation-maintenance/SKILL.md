---
name: documentation-maintenance
description: Use when changing project behavior, architecture, schemas, APIs, editor controls, runtime guarantees, setup, or documentation; maintain architecture and user guides alongside implementation and validate Mermaid diagrams and links.
---

# Maintain project documentation

Read [project-guide](../project-guide/SKILL.md), then the [documentation map](../../../docs/README.md). Use [technical-writing](../technical-writing/SKILL.md) with its [local notes](../technical-writing/SOURCE.md) for clear task-based instructions, and [mermaid-diagrams](../mermaid-diagrams/SKILL.md) with its [local notes](../mermaid-diagrams/SOURCE.md) for useful visual explanations. Preserve upstream skill files.

## Update the owning documents with the change

Check what changed before writing. Architecture/package boundaries, persistence, runtime ownership, trust boundaries, and durable versus local guarantees belong in `docs/architecture.md`. Product tasks, exact UI labels, save/publish/run/replay steps, recovery from errors, and capability limitations belong in `docs/user-guide.md`. Public API/schema and embedding changes also update their focused references. `docs/design.md` owns future direction; `docs/implementation.md` owns shipped status. Keep both distinct from the current architecture and user guide. Link detailed references from the documentation map and keep README onboarding short.

For each behavior change, update affected architecture and user-guide sections in the same milestone, or record why each is unaffected. Do not mechanically change every page. Remove or qualify obsolete claims. Never show proposed components as deployed, a deterministic fixture as an LLM, a running process as durable recovery, or a hash as authorization.

## Explain with diagrams

Use Mermaid when it clarifies component relationships, lifecycle, data flow, state, or a multi-step user journey. Keep one concept per diagram, readable labels, and a nearby prose explanation or numbered procedure so meaning does not depend on color or rendering. Use a flowchart for structure/journeys, sequence for interactions, state for lifecycle, and ER for persistence when helpful. Avoid diagrams for single facts, decorative graphs, or speculative deployment topology.

Store diagrams in fenced `mermaid` blocks beside their explanation. Update them whenever their underlying behavior changes. Use quoted labels for punctuation. Do not embed secrets, authenticated URLs, scripts, remote assets, or clickable actions. Prefer renderer-portable syntax. Keep current and proposed diagrams on explicitly labeled pages.

## Verify

Exercise documented application commands and representative user tasks through implemented controls or tests. Cross-check architecture edges against project references and composition code; cross-check labels and API/schema examples against current code. Run `python3 scripts/check_docs.py` for local links and document structure, then add `--mermaid` for syntax/render verification using the pinned development renderer. Install the documentation-only dependency with `npm ci --prefix tools/documentation`; the application does not consume it. Use `--browser-executable` for an existing Chromium or Playwright's installed browser. Do not upload project diagrams to external tools.

Review reader questions: How do I start? What is saved versus published? Does closing the browser stop execution? How do I restore a version? What permissions does an import grant? What remains unsupported? The docs should answer without conversation context. Report unrun commands or unsupported renderer/browser checks accurately. Commit and push according to the user's existing authorization.
