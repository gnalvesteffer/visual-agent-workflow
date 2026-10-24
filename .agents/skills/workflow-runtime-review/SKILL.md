---
name: workflow-runtime-review
description: Use when changing Agent Framework translation, execution semantics, parallelism, loops, retries, cancellation, approvals, checkpoints, or durable recovery.
---

# Workflow runtime review

Read [project-guide](../project-guide/SKILL.md). Use [microsoft-agent-framework](../microsoft-agent-framework/SKILL.md) to verify version-specific framework behavior before reviewing execution semantics and recovery here.

## Define semantics before implementing

For each operation specify input/output types, data versus control connections, ordering, join policy, loop bounds, error handling, retries, cancellation, and output completion. Identify how it maps to Agent Framework executors/workflows. Avoid a second implicit scheduler with conflicting semantics.

Runs bind to immutable revisions and dependency identities. Definition edits and activation must not mutate active runs. Browser connection lifetimes cannot own execution or approvals.

## Exercise relevant failure boundaries

- UI disconnect/reconnect while execution continues.
- Worker failure before and after state/checkpoint persistence; verify ownership prevents concurrent recovery of the same work.
- External action completes but its response/checkpoint is lost. Mark uncertain outcomes and reconcile rather than blindly retry.
- Duplicate delivery and retry attempts; preserve invocation identity and side-effect policy.
- Parallel completion, join failure, bounded loops, cancellation, and backpressure.
- Approval persists across process restart and resumes only the matching request once under policy.

Use deterministic executors, a controlled MCP fixture, and explicit synchronization/fault injection instead of timing-dependent sleeps. Select cases relevant to the change. Verify durable storage and restart behavior with integration tests; in-memory mocks alone cannot establish recovery.

Do not promise universal exactly-once external effects. Separate browser independence from crash recovery and checkpoint recovery from historical playback. Apply [audit-playback-review](../audit-playback-review/SKILL.md) when changing event instrumentation.

## Verified local pause boundary

The current runtime parks the same in-process Agent Framework continuation at awaited node boundaries and before completion. Paused contexts release the shared execution permit but still count against the 32-context admission budget. Test current-node completion, no next-node admission, cancellation while parked, and other work progressing; do not describe this as durable checkpoint recovery. Restart interrupts unfinished owners. See [run control](../../../docs/run-control.md).
