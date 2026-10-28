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
checkpoint round-tripping, failure/publication race handling, and the remaining
exact admission restrictions are proven.

The trusted envelope validator also binds the frame back to the outer
checkpoint: its evidence cursor must equal the committed sequence, its native
payload and hash must equal the envelope payload and checksum, and its variable
snapshot hash must equal the captured native variable state. The loop cursor's
iteration must match the corresponding recorded loop event. These checks reject
component substitution before native recovery; they do not by themselves make
schema 1.45 production-admissible.

A test-only adapter seam exercises the real Agent Framework native checkpoint
serializer for a schema 1.45 loop-variable graph. It suspends after a body
write, publishes the composite frame with the checkpoint/evidence transaction,
and resumes it twice from independent test processes. It also holds the owner
after publication, terminates that process, and recovers from the released
ownership handle in a fresh process. The proof preserves the evidence prefix
and monotonic versions without duplicating committed writes. A separate
deterministic boundary test distinguishes cancellation before capture from
cancellation after a captured suspension. It is still not durable recovery
admission: the test seam bypasses production host admission, and
failure/publication races plus the remaining promotion restrictions are not
established by this proof.

## Required promotion gates

- [x] Prove the native payload preserves loop source/port and iteration state
  through the real Agent Framework serializer in a test-only seam.
- [x] Prove the same boundary in independent test processes against the
  durable SQLite checkpoint/evidence transaction.
- [x] Persist and validate the composite frame, including the exact evidence
  prefix, loop anchor, variable snapshot, and component/composite hashes, in
  the test-only loop proof path.
- [x] Capture native state, variable state, loop cursor, event prefix, and
  suspension marker at one quiescent boundary.
- [x] Resume twice without duplicating completed Set/Get operations or skipping
  a body activation; preserve monotonic variable versions and loop stop reason.
- [x] Distinguish cancellation before capture from cancellation after a
  captured suspension; preserve the saved boundary in the latter case.
- [x] Recover after the owner process is terminated after durable publication;
  verify ownership release, exact frame identity, and no duplicate writes.
- [x] Bind the trusted outer checkpoint envelope to the proof frame's native
  payload, evidence sequence, variable snapshot, and recorded loop iteration;
  reject mismatched components before recovery.
- [x] Compare the captured variable snapshot with the recorded variable
  evidence prefix when state events are present; reject a snapshot that is
  internally hashed but disagrees with its passive history.
- [x] Require a declared source port to target the declared next activation in
  the authored execution graph; reject hash-consistent cursor substitution.
- [x] Inject loop checkpoint publication failure and verify the SQLite
  transaction leaves no capture marker, checkpoint pointer, or resumable
  boundary.
- Reject stale, advanced, foreign, malformed, oversized, tampered, terminal,
  and unsettled frames before native execution begins.
- Preserve schema 1.44 and older checkpoint formats and behavior unchanged.

See [the loop-variable topology decision](0030-loop-variable-topology-proof.md)
and [the existing variable checkpoint frame](0028-variable-checkpoint-frame.md).
