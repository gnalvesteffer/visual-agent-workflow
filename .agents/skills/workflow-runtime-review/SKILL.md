---
name: workflow-runtime-review
description: Use when changing Agent Framework translation, execution semantics, parallelism, loops, retries, cancellation, approvals, checkpoints, or durable recovery.
---

# Workflow runtime review

Read [project-guide](../project-guide/SKILL.md). Use [microsoft-agent-framework](../microsoft-agent-framework/SKILL.md) to verify version-specific framework behavior before reviewing execution semantics and recovery here.

## Define semantics before implementing

For each operation specify input/output types, data versus control connections, ordering, join policy, loop bounds, error handling, retries, cancellation, and output completion. Identify how it maps to Agent Framework executors/workflows. A successful no-result exit is distinct from an empty output, failure, or cancellation: preserve its terminal status, caller outcome, and playback semantics. If the framework requires one output executor, keep any adapter-only handoff out of the serialized graph and visible transfer evidence. Avoid a second implicit scheduler with conflicting semantics.

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

On .NET 10, a Generic Host can stop immediately after admission before `BackgroundService.ExecuteAsync` starts. Its loop `finally` may never run. Preserve the immediate-stop regression and finalize pending ownership after the background task completes (or never existed), including when the loop did not start. A stop deadline expiring while a worker remains active does not authorize marking its journal terminal underneath its event writer.

Conditional graphs carry one string value along one selected route. Every output connects once, branch nodes select one true/false port, and exclusive merges accept the selected incoming value without waiting for other arms. Schema-1.18 `core.terminate` nodes are successful string-path exits with no output pin; record `run.terminated` without a result and preserve the termination node identity for playback. Test the chosen terminating arm, the alternate returning arm, terminal storage, and no phantom visible transfer to the framework's internal output sink. Preserve reachability validation and real chosen/unchosen/cancellation/merge-edge tests. For bounded loops, ignore only the one structurally validated body-return edge during cycle checks; bound the body, preserve iteration state in the private message, check cancellation at every node boundary, and distinguish edge delivery by both source node and source pin. Verify predicate exit, cap exit, audit events and playback against the real framework runner. Do not turn an exclusive merge into a parallel barrier or use topological display order as runtime sequencing.

The implemented approval gate is process-owned: journaled request/decision evidence does not restore a continuation after restart. Test reserved tool invocation IDs, exact action hashes, idempotent/conflicting decisions, cancellation/owner loss and independent manual pause. Never auto-approve on resume or retry an approved external effect on restart. Approval waits release the active execution permit and retain the bounded admission slot.

Runtime/controller storage now uses `IWorkflowRunStore`. Validate event/state batches atomically with the expected last sequence. A persistence conflict is not permission to retry external work. SQLite serializes storage writes but provides no runtime lease: startup interruption still requires one exclusive owner. See [run storage](../../../docs/run-storage.md).

Built-in local stores now expose `ILocalRuntimeOwnershipStore`; acquire before startup interruption and retain until all writers stop. Cleanup must run once: an old host's repeated Stop/Dispose must not interrupt a new owner. Saved deterministic suspension recovery is opt-in via `CheckpointWorkflowHost`, not a change to ordinary pause/startup. Reject advanced evidence, stale checkpoint IDs and agent/MCP/approval graphs; never infer generic crash recovery from the native round-trip proof.

Recovery admission now rejects unsettled or ambiguously correlated evidence. A terminal run status does not settle external outcomes; post-authorization tool failures remain unknown without corresponding completion. A read-only reconciliation report neither acquires a lease nor authorizes replay. Preserve the public stale-approval conflict type across cancellation/terminal-cleanup races rather than leaking `WorkflowRunControlException` from an approval decision.
