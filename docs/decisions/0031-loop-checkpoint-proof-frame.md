# Decision 0031: loop checkpoint proof frame

**Status:** Proof-only frame shipped; schema 1.45 checkpoint admission remains closed.

**Date:** 2026-10-28

## Context

Schema 1.45 can execute one bounded explicit loop with scalar mutable
variables, but the checkpoint host currently admits only deterministic linear
graphs. The native Agent Framework cursor carries private loop transport state,
while variable state and the recorded event prefix are owned by the adapter and
host. Enabling recovery by merely adding `Loop` and `LoopBody` to the allowlist
could resume with a loop cursor, variable snapshot, and evidence prefix from
different logical boundaries.

## Decision

Add `WorkflowCheckpointFrame` as a Core proof contract. It binds a versioned
opaque native JSON payload, the exact schema/revision/content identity, a
loop/body cursor assertion, a complete `WorkflowVariableSnapshot`, the
contiguous evidence cursor and prefix hash, component hashes, and a composite
frame hash. Validation checks the pinned definition, scalar snapshot, native
JSON bounds, loop topology, declared execution ports, and the loop evidence
anchor.

The frame is not portable workflow data, ordinary playback state, or a second
scheduler. It is not wired into `CheckpointWorkflowHost` yet. Production
admission remains fail-closed for schema 1.45 until native Agent Framework
checkpoint round-tripping, atomic capture/publication, repeated suspension and
resume, cancellation/failure races, and process-loss behavior are proven.

## Required promotion gates

- Prove the native payload preserves loop source/port and iteration state in a
  fresh host/process.
- Capture native state, variable state, loop cursor, event prefix, and
  suspension marker at one quiescent boundary.
- Resume without duplicating completed Set/Get operations or skipping a body
  activation; preserve monotonic variable versions and loop stop reason.
- Reject stale, advanced, foreign, malformed, oversized, tampered, terminal,
  and unsettled frames before native execution begins.
- Preserve schema 1.44 and older checkpoint formats and behavior unchanged.

See [the loop-variable topology decision](0030-loop-variable-topology-proof.md)
and [the existing variable checkpoint frame](0028-variable-checkpoint-frame.md).
