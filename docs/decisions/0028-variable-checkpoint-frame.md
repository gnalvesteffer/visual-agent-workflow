# Decision 0028: variable checkpoint frame proof gate

**Status:** In progress; schema 1.44 checkpoint admission remains fail-closed.

**Date:** 2026-10-28

## Context

Schema 1.44 variables are run-scoped mutable state. The existing native
checkpoint adapter persists framework scheduling state and a run/evidence
cursor, but it does not persist the `WorkflowVariableStore`. Re-enabling
checkpoint admission without that state would resume reads from declaration
defaults and could reuse variable versions.

The required adversarial review completed with independent proponent,
opponent, and adjudicator roles. The adjudicator recommended revising the
candidate into a composite, definition-bound recovery frame before enabling
schema 1.44 recovery.

## Decision

Use a versioned trusted recovery frame that binds native framework state,
variable state, and the evidence cursor at one quiescent post-node boundary.
The frame must include every declared scalar variable, its exact type,
portable scalar value, version, the store's `nextVersion`, schema/revision/
content identity, a bounded size, and an integrity hash. Restore and validate
the complete frame before constructing or resuming executable activation.

The first implementation step is the Core `WorkflowVariableSnapshot` contract:
it captures all values under the store lock and restores only exact declaration
IDs/types with valid scalar values and monotonic versions. It is runtime
checkpoint state, not workflow package data or playback evidence.

Checkpoint admission stays rejected until the composite frame, atomic
publication, resume ordering, corruption/size/identity checks, cancellation
behavior, repeated suspend/resume, and process-loss tests are complete.
Loop, parallel, Sequence, collaboration, child-workflow, graph-function,
external-capability, and asynchronous state remain outside this proof.

## Consequences

- Existing checkpoint envelopes and pre-1.44 recovery behavior remain unchanged.
- A variable snapshot cannot be interpreted as a portable workflow value or a
  replacement for event-based playback reconstruction.
- Variable values are bounded non-secret scalar state, but checkpoint storage
  remains trusted local runtime state and is not an authentication boundary.
- The roadmap may mark only the snapshot/restore contract complete; it must not
  mark variable-aware recovery complete until the full frame is proven.

## Evidence

- `WorkflowVariableStore.Snapshot` and `FromSnapshot` implement the first gate.
- `MutableVariableContractTests` cover all scalar types, version continuity,
  identity/type/value rejection, and invalid counters.
- `WorkflowCheckpoints.ValidateCheckpointGraph` explicitly rejects schema 1.44
  until the remaining frame gates are implemented.
