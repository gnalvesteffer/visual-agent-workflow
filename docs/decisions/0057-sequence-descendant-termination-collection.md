# Decision 0057: Sequence descendant-termination collection proof

## Status

Accepted and implemented as a proof-only runtime increment.

## Context

The schema 1.56 typed-array contract keeps execution separate from pure data
evaluation. The next reviewed Blueprint-parity candidate would let a typed
predicate choose an execution arm, but the production Sequence builder did not
collect a descendant `Terminate` signal. A false predicate could therefore
execute a termination node without settling its Sequence activation.

## Decision

Route every descendant `Terminate` signal that carries Sequence lineage through
the existing `SequenceProductionCollector`. The collector maps a failure
termination to `SequenceTerminalState.Failed`, settles the exact output
activation once, emits the normal `SequenceOutputFailed` and `SequenceFailed`
evidence, and lets the parent runner emit its failed terminal outcome.

The collector does not infer success from a missing value. Successful
no-result termination is deliberately not admitted by this proof because its
parent result contract, final status, and passive playback representation need
separate qualification.

The change is runtime proof infrastructure only. It does not add schema 1.57,
change validation, add Studio authoring, or reinterpret existing persisted
graphs.

## Evidence and verification

The internal production proof exercises a Sequence output whose descendant is a
failure `Terminate`. The regression requires:

- no hang or orphaned Sequence activation;
- one `SequenceOutputFailed` for the terminated output;
- one `SequenceFailed` event;
- a failed parent result with no output;
- no root `Completed` event.

The data-driven array predicate remains deferred until this collector proof is
combined with an explicit schema-qualified result and replay contract.
