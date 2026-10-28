# Bounded Sequence dispatch boundary

**Status: Deferred pending framework proof**  
**Scope: Blueprint-inspired control flow**  
**Decision date: 2026-10-27**

## Context

Unreal Blueprint provides a Sequence control-flow node that exposes multiple ordered execution outputs. Visual Agent Workflow does not currently have an equivalent. The current explicit graph supports linear activation, exclusive routing, bounded loops, and fork/barrier regions, but the Agent Framework runner has no general activation cursor or run-level accounting for an unjoined fan-out.

The distinction matters:

- **Dispatch order** means the runtime offers output activations to the scheduler in a declared order.
- **Completion order** means downstream work for output 1 finishes before output 2 is admitted.
- **Fork/barrier** means downstream branches may overlap and a join explicitly accounts for their completion.

These are different contracts. Calling an unjoined fan-out “Sequence” without specifying the distinction would make the graph appear more deterministic than its runtime behavior.

## Decision

Do not add `core.sequence` to a public workflow schema or Studio palette yet. The next work is a non-public framework proof against the pinned Agent Framework version and a scheduler/evidence design that answers:

- which activations are live after dispatch;
- when the run is allowed to complete;
- what partial dispatch, downstream failure, cancellation, pause, and host stop mean;
- how skipped outputs and unknown external outcomes are recorded;
- how playback distinguishes output identity from completion order;
- whether restart/resume is explicitly unsupported or requires a persisted cursor.

If the proof passes, the candidate public contract is deliberately bounded:

- schema 1.38 only;
- `core.sequence` with exactly 2–4 statically authored execution outputs;
- stable output IDs and explicit canonical order;
- no data pins, dynamic fan-out, capabilities, retry, loop, nested Sequence, implicit join, rollback, or exactly-once side effects;
- no durable checkpoint recovery or restart resumption in the first release;
- existing host grants, approvals, Agent/MCP behavior, and child-workflow contracts remain unchanged.

The node must not be implemented as a visually convenient multi-edge fan-out until the scheduler proof demonstrates the actual semantics. A completion-ordered Sequence would require a continuation cursor and explicit one-at-a-time admission; a dispatch-only Sequence must still account for active downstream work and terminal run state.

## Required pre-schema proof

The proof must use synchronized deterministic executors rather than timing sleeps and must cover:

1. Declared output IDs and order survive a canonical definition/package round trip.
2. The pinned Agent Framework actually admits multiple outputs with the observed ordering.
3. Active downstream work cannot cause the run to report completion early.
4. Partial dispatch, downstream failure, cancellation, pause, and host stop have explicit outcomes.
5. Output identity, invocation identity, connection identity, and ordinal are sufficient for passive playback.
6. Playback never invokes an Agent, MCP tool, approval, child workflow, or extension.
7. Legacy schemas 1.0–1.37 remain unchanged and reject any unsupported future node cleanly.

Only after these checks pass should the implementation add a schema version, validator rules, node descriptor, editor controls, event kind, package compatibility, and runtime mapping together as one vertical slice.

## Rejected alternatives

- **Implicit sequential fan-out:** rejected because downstream asynchronous work may overlap and the graph would falsely promise completion ordering.
- **Treating Sequence as ParallelFork without a join:** rejected because it hides active-activation and failure accounting while providing no barrier semantics.
- **Mutable variables as the next parity slice:** deferred because branch, loop, parallel-write, checkpoint, and replay semantics are not defined; immutable parameters remain the safe current model.
- **Exposing a palette card before runtime support:** rejected because editor discoverability is not evidence of backend support.

## Consequence

This is a deliberate progress gate, not a claim of Blueprint parity. Existing workflows should use ordinary sequential edges when completion ordering is required and the existing bounded fork/join when concurrent work with accounting is required. The roadmap tracks Sequence as deferred until the proof and contract are complete.

