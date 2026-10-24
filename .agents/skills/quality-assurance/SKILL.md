---
name: quality-assurance
description: Use when planning verification for behavior changes, reviewing test coverage, reproducing defects, or selecting runtime, API, browser, and regression checks.
---

# Quality assurance

Read [run and validate](../run-and-validate/SKILL.md). For browser work apply [webapp testing](../webapp-testing/SKILL.md) and its [local guidance](../webapp-testing/SOURCE.md). Choose domain reviews from the [catalog](../../../docs/agent-skills.md); do not run every review on every change.

## Select checks from observable risks

State the expected user behavior and the failure that matters. Map each changed guarantee to the lowest useful test layer: Core invariants, actual Agent Framework adapter behavior, hosting/persistence and MCP integration, API responses, or Playwright user journeys. Include relevant negative cases, compatibility, cancellation and ownership boundaries. Prefer tests that would fail if the behavior were broken over tests that repeat implementation details. Reversible documentation or cosmetic edits do not require artificial unit tests.

Use deterministic providers and disposable local MCP fixtures. Concurrency checks should synchronize on explicit barriers or observed state with bounded deadlines; avoid sleeps that merely hope a race occurred. For run control, verify node admission, event ordering, idempotent commands, cancellation while waiting, immutable revision identity, admission limits, and owner loss. In-flight work must finish before a cooperative pause is acknowledged; replay must never execute tools.

## Verify through the real boundary

Build with warnings as errors, run affected tests, and exercise changed public controls or APIs. For browser changes inspect rendered roles/names, keyboard-relevant controls, loading/error states, reconnect, and persisted evidence. Add a regression for a meaningful discovered defect. Accessible names must separate node ID, type, and state; visually separate elements alone do not guarantee a usable accessible name.

Reuse the repository's existing test projects and scripts. Do not add frameworks, snapshot packages, Docker dependencies, or coverage targets without a concrete need. Live providers, arbitrary user assemblies, multi-user security and production recovery remain unsupported until separately implemented and tested. Screenshots, coverage percentages and successful builds alone cannot establish these guarantees.

## Report evidence precisely

Record commands, actual results, and material untested behavior in the owning milestone documentation. Distinguish failed, blocked, skipped and unrun checks. Reproduce failures, fix the cause, rerun the affected checks, and broaden regression only where change impact warrants it. Never call a flaky test passed by retrying without investigation. Update [run and validate](../run-and-validate/SKILL.md) when a new procedure is exercised successfully.

When starting a new run while an old completed graph remains visible, browser checks must wait for the selected run identity to change before clicking its output. A completed node from the previous run is not proof the new run has been admitted; slower storage exposed this race in the maximum-input UI check.

For reconciliation tests include terminal status with an unresolved external call, repeated/ambiguous legacy calls, failure before versus after authorization, cursor races, unsupported sources, source replacement and loss. Assert inspection leaves evidence unchanged and excludes prompts/results/native state. A canceled approval can race terminal cleanup; public approval decisions must consistently return their documented conflict type.
