---
name: adversarial-review
description: Use before proposing or implementing consequential product behavior, workflow/runtime architecture, public contracts, security boundaries, migrations, or other decisions with materially different plausible approaches and meaningful failure cost.
---

# Adversarial review

Use this review before committing the work to a consequential design direction. It is a decision-quality check, not an implementation delegation. For routine coding after the design is settled, do not repeat the full review. If routine implementation exposes a new consequential choice, pause at that choice and review it before proceeding.

Read [project-guide](../project-guide/SKILL.md), the relevant design/roadmap documents, and [agent-orchestration](../agent-orchestration/SKILL.md). The user's stated outcome and constraints govern the review; reviewers may identify risks and propose alternatives but cannot silently change scope or override an explicit user choice.

## When to use the full review

Run the three roles for decisions involving one or more of:

- New or materially changed product behavior, graph semantics, or user-facing authoring concepts.
- Runtime, persistence, recovery, scheduling, concurrency, or host-integration architecture.
- Public APIs, serialized contracts, schema evolution, imports/exports, or compatibility boundaries.
- Security, credentials, authorization, tool access, isolation, or trust boundaries.
- A cross-layer change or a decision with multiple plausible designs and meaningful cost if wrong.

Skip the full review for mechanical edits, routine implementation of an already reviewed design, focused documentation, test-only maintenance, and clear localized fixes. A direct user request to adversarially review a specific idea always triggers it, even if the idea appears small.

## Three independent roles

Use three distinct, clean-context subagents when the platform supports them. Give each only the original user intent, relevant constraints and authoritative repository sources, and the specific decision under review. Do not forward unrelated conversation, private data, or the coordinator's speculative reasoning.

1. **Proponent:** Make the strongest evidence-backed case for a viable design that fulfills the user's intent. State the decision, benefits, assumptions, tradeoffs, and what evidence would validate it. Do not implement or edit files.
2. **Opponent:** Independently identify concrete failure cases, counterexamples, security/compatibility risks, hidden costs, and the strongest alternative. If there is a candidate design, assess that design directly; do not see the proponent's argument before submitting. Do not invent objections solely to appear adversarial. Do not implement or edit files.
3. **Adjudicator:** Receive the original brief, governing instructions and sources, and both submitted arguments. Recommend **proceed**, **revise**, or **reject**. Resolve disagreement using evidence, distinguish confirmed facts from assumptions, state material unknowns, and identify any changes needed before implementation. Do not implement or edit files.

Proponent and opponent may work in parallel if each gets an independent clean context. The adjudicator runs only after both reports are complete. Where a candidate proposal is needed to focus the opponent, provide the proposal itself, not the proponent's reasoning. Keep each report concise and cite repository files, tests, official references, or reproducible evidence when available.

## Decide and proceed

The coordinator owns the final decision and all implementation, testing, documentation, and integration. Treat adjudication as a reasoned recommendation, not a veto. Follow the user's explicit decision when it settles a product preference; still surface concrete implementation, safety, or compatibility conflicts and offer the closest safe way to honor the request.

- **Proceed:** implement the reviewed direction and its validation plan.
- **Revise:** incorporate the adjudicator's required design changes, then re-review if the revision changes the core proposal or crosses another consequential boundary.
- **Reject:** explain why the candidate fails the user's goal and implement only a reviewed alternative, or report the blocker if no viable alternative is authorized.

Do not claim independent adversarial review if three distinct agents did not participate. If subagents are unavailable, report that the gate is incomplete and why. For low-risk, reversible work, the coordinator may continue with an explicitly labeled self-review; for a consequential decision, pause before implementation and surface the limitation rather than presenting a simulated triad as independent evidence. Never let a review stall unrelated work.

Record durable decisions and changed contracts in the appropriate design/architecture document, not only in agent messages. Update the roadmap when scope or milestone ordering changes. Use [documentation-maintenance](../documentation-maintenance/SKILL.md) for documentation expectations.

## Provenance

This is project-authored guidance. The repository's [agent-orchestration skill](../agent-orchestration/SKILL.md) links the reviewed Superpowers guidance for independent delegation and integration. A general upstream brainstorming skill was also reviewed, but no maintained upstream skill was found that matched this repository's requested proponent/opponent/adjudicator gate closely enough to vendor. This procedure is adapted to the project's user-authority, architecture, and implementation workflow; it does not copy third-party text.
