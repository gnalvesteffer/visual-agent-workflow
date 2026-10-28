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

## Current proof evidence

The non-public `SequenceDispatchProofTests` test exercises Agent Framework 1.26.0 with two synchronized downstream executors. It passed and established:

- the dispatcher can queue `first` and then `second` in that order;
- downstream executor start order is not guaranteed by that dispatch order (the observed run started `second` before `first`);
- the second executor can start while the first remains blocked and active;
- completion order can therefore differ from dispatch order (the proof observes `second`, then `first`).

The proof also covers the first cancellation boundary: when the dispatcher is
held after the first send and before the second send, cancelling the framework
run prevents the second output from being dispatched or started. This is only a
local framework observation; it does not yet prove a public workflow Sequence,
because there is still no VAW activation ledger, descendant completion contract,
or passive playback lineage for a multi-output node.

This is useful evidence, not a failure of the framework. It rules out treating ordinary `SendMessageAsync` fan-out as a Blueprint Sequence. A public Sequence must either define this as an explicitly concurrent dispatch primitive with full active-work accounting, or add a separate continuation mechanism that admits the next output only after the previous path's documented completion boundary.

## Adjudicated next slice

The required three-role design review (proponent, opponent, and adjudicator) rejected a public schema change at this stage. The existing runner has one framework output sink, no descendant-completion ledger, no per-output lineage in `GraphMessage`, and no defined inter-dispatch pause boundary. A public node would therefore be able to report completion while work from another output remained active.

The first implementation slice is now a non-public `SequenceActivationLedger`. It is deliberately limited to in-process proof work: two to four stable output IDs, one active output at a time, explicit dispatched/completed/failed/cancelled/skipped states, and terminal failure/cancellation that marks later outputs skipped. It is not serialized, exposed in the Studio palette, or connected to the workflow runner. Its focused tests prove the bounded continuation contract and prevent a future implementation from using raw framework fan-out as a substitute for completion accounting.

The runner now has a separate `ExecutionActivationLedger` integrated at the event boundary. It accounts for active node invocations by scope, node, and invocation ID, and rejects normal workflow completion or termination while any invocation lacks a terminal node event. A non-persisted `SequenceRuntimeCoordinator` now proves stable Sequence invocation, output, ordinal, and connection identity across dispatched, completed, failed, cancelled, and skipped lifecycle facts; failed and cancelled sequences receive distinct terminal facts rather than a misleading completion event. Core now defines a versioned passive `SequenceLifecycleEvidence` payload and reader for those lifecycle event kinds, including fail-closed malformed and unknown-version handling. The coordinator can map its proof events to recorded `ExecutionEvent` records, but the real runner does not emit them yet. These are evidence contracts, not public Sequence execution. The next proof must connect Sequence-specific state to the runner-owned boundary, emit the lifecycle facts through real run recording, and exercise pause, host stop, and partial-dispatch outcomes against the real runner. Until those checks pass, schema 1.38 remains reserved and deferred.

## Rejected alternatives

- **Implicit sequential fan-out:** rejected because downstream asynchronous work may overlap and the graph would falsely promise completion ordering.
- **Treating Sequence as ParallelFork without a join:** rejected because it hides active-activation and failure accounting while providing no barrier semantics.
- **Mutable variables as the next parity slice:** deferred because branch, loop, parallel-write, checkpoint, and replay semantics are not defined; immutable parameters remain the safe current model.
- **Exposing a palette card before runtime support:** rejected because editor discoverability is not evidence of backend support.

## Consequence

This is a deliberate progress gate, not a claim of Blueprint parity. Existing workflows should use ordinary sequential edges when completion ordering is required and the existing bounded fork/join when concurrent work with accounting is required. The roadmap tracks Sequence as deferred until the proof and contract are complete.

