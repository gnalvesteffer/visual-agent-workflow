---
name: agent-orchestration
description: Use when a task may benefit from parallel subagents, delegated implementation, independent reviews, or coordinated multi-agent work in this repository.
---

# Agent orchestration

Delegate only when the work can be split into independent, bounded outcomes. The coordinating agent remains responsible for the user request, integration, tests, documentation, and final report.

## Decide whether to delegate

- First identify dependencies and likely file ownership. Parallelize separate investigations or changes that can be integrated independently.
- Keep tightly coupled architecture, broad refactors, shared-state debugging, and edits to the same files with one implementer. Parallel effort is not useful when agents must continually reconcile each other's intermediate changes.
- Delegate read-only research or review when implementation must remain serial. A reviewer should report findings and proposed fixes without editing the implementer's files.
- Use the smallest number of agents that meaningfully reduces elapsed work. Delegation adds briefing, integration, and review costs.

## Write self-contained work briefs

Each brief states:

1. The outcome and why it matters to the current task.
2. Exact scope, owned files or subsystem, and files that must not be edited.
3. Relevant contracts, constraints, and decisions already made.
4. Expected tests or other evidence, plus what the agent should report.
5. Dependencies on other work and whether the assignment is implementation or read-only review.

Do not ask an agent to rediscover the whole conversation. Provide the minimal context needed, and point it to authoritative repo guidance and the focused skill.

## Coordinate shared repository state

- Inspect Git status before dispatch and preserve pre-existing user changes.
- In a shared working tree, assign disjoint files. Never have two agents modify the same file concurrently; sequence those changes or request read-only analysis from one agent.
- Tell agents when the workspace is shared and whether their changes are expected to appear directly in the common tree. Avoid cherry-picking or reapplying edits that are already present.
- Do not let parallel agents commit, push, publish, deploy, or message external parties unless the user explicitly authorized that operation and the coordinator assigned it.
- Keep one integration owner. They inspect the complete diff, resolve overlaps, run the combined checks, and update docs.

## Enable agent-to-agent collaboration

- Treat subagents as separate contributors, not as a shared chat with automatic context. Name the coordinator, collaborators, their scopes, and the shared outcome in each assignment.
- Give collaborators a direct channel for short, actionable coordination when the platform provides one. Use it to ask for a contract decision, report a dependency, or request a handoff; do not broadcast routine progress to every agent.
- When one agent's result affects another, send the relevant finding or contract directly to the affected owner and the coordinator. The receiver should acknowledge consequential decisions before building on them.
- Keep a single source of truth for agreed interfaces, sequencing, and unresolved decisions: the task brief/ledger or an owned design document. Chat messages are coordination, not durable project documentation.
- The coordinator resolves incompatible proposals, assigns any shared-file integration work, and tells affected agents which decision is authoritative. Agents should not independently negotiate conflicting public APIs or schema contracts and then both implement them.
- Collaboration messages must not ask another agent to perform work outside its assigned scope. If the scope needs to change, the coordinator updates the brief and ownership first.

## Integrate and verify

- Read every completion report and inspect the actual diff; a report is not evidence that changes are correct.
- Check for conflicting assumptions, duplicated implementations, API/schema drift, missing documentation, and tests that cover only internal details.
- Run focused tests for each changed area, then the broadest relevant suite once integrated. Rerun checks affected by integration fixes.
- For reviewer findings, distinguish confirmed defects from suggestions. The coordinator owns fixes and records any deliberately deferred issue.
- Summarize which pieces were delegated, what was integrated, validation results, and unresolved concerns to the user.

## Resume across context compaction

For multi-turn work, use the repository roadmap and implementation history as durable status. Keep a concise task ledger in the active task workspace when one exists; do not turn temporary coordination notes into product documentation. After compaction, reconcile the ledger with Git status, commits, and test evidence before assigning or repeating work.

## Source and adaptation

This repo-specific guidance adapts the independence, focused-brief, and integration-review ideas from:

- [Superpowers: Dispatching Parallel Agents](https://github.com/obra/superpowers/blob/main/skills/dispatching-parallel-agents/SKILL.md)
- [Superpowers: Subagent-Driven Development](https://github.com/obra/superpowers/blob/main/skills/subagent-driven-development/SKILL.md)

Those skills assume workflow and tool capabilities that may not apply here. Follow this repository's `AGENTS.md`, current platform instructions, and the user's authorization whenever they differ. This skill does not itself authorize delegation, worktree creation, commits, pushes, or other external side effects.
