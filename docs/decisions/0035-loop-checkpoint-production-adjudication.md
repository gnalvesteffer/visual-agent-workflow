# Decision 0035: keep loop checkpoint recovery fail-closed during production qualification

**Status:** Adopted; default schema 1.45 admission remains closed. An explicit
experimental host capability now exercises the shared frame path, but is not a
production-readiness claim.

**Date:** 2026-10-28

## Context

The loop-variable slice has a test-only proof frame and independent-process
coverage. That evidence is useful, but it does not qualify the public
`CheckpointWorkflowHost` path. The public sink currently persists the native
checkpoint and legacy variable hash, not the composite frame that binds native
cursor state, loop position, variable state, evidence prefix, and suspension.

## Adjudication

An adversarial proponent/opponent/adjudicator review considered whether to
promote the proof seam into production admission. The adjudicator recommended
**revise**: retain the proof work, but do not widen the schema allowlist yet.
The opponent wins on current readiness because callback/native-state pairing,
public-host publication races, negative admission, passive playback from
production-generated loop history, and process-loss behavior are not proven on
the production boundary. The proponent's bounded, capability-gated design is
the target once those gates are closed.

A second clean-context proponent/opponent/adjudicator review on 2026-10-29
reconsidered promotion after the publication, cancellation, disposal, and
process-loss gates were qualified. The adjudicator recommended **revise with a
narrower promotion**: keep the explicit host capability opt-in and fail-closed
default, and do not present schema 1.45 as generally supported until the native
checkpoint compatibility contract and public API status are made explicit.

## Decision

`WorkflowRunner.ValidateCheckpointGraph` rejects schema 1.45 with a specific
message explaining that production recovery requires one composite checkpoint
frame. Schema 1.44 and older checkpoint envelopes remain unchanged. The proof
seam remains test-only and is not evidence that the public host can recover a
loop.

## Promotion checklist

- [x] Carry the actual quiescent boundary observation through the shared host
  sink, fail closed when callbacks are missing/active/unstable, and bind it to
  the exact native checkpoint identity. The public host sink now constructs a
  composite frame from that observation when a loop suspension is eventually
  admitted; schema 1.45 admission remains closed while the later transaction
  and race gates are completed.
- [x] Persist the composite frame, evidence prefix, and suspension marker in
  one host-owned transaction; acknowledge suspension only after commit. The
  SQLite store commits the evidence transition and checkpoint pointer together,
  and publication-failure regressions prove neither half survives rollback.
- [x] Add a fail-closed acknowledgement-reconciliation guard: an uncertain
  publication may be adopted only when the reopened run is suspended, resumable,
  and its checkpoint ID and native payload exactly match the attempted capture.
- [x] Qualify lost acknowledgement handling through the host/store boundary:
  a transactional test store commits the checkpoint and drops the return value;
  the host reopens and adopts the exact suspension without terminalizing it.
- [x] Add an explicit host capability that defaults disabled and apply the same
  exact topology/frame admission to both start and resume. The opt-in host is
  intentionally experimental and is not enabled by the Studio/default host.
- [x] Exercise the opt-in public host through two successive suspensions and
  reconstruct its variable evidence prefix passively, without another
  workflow execution for the playback assertion.
- [x] Recover an opt-in public-host suspension after the owner process is
  terminated, then resume twice without duplicating committed loop-body writes.
  Checkpoint validation now checks the exact recorded evidence prefix rather
  than incorrectly treating later history as part of the saved frame.
- [x] Make recovery assessment total at the persisted-state boundary: malformed
  snapshots, checkpoint envelopes, frame data, and evidence are reported as
  ineligible rather than escaping as inspection/recovery exceptions. Cancellation
  and process-fatal failures remain outside this normalization boundary.
- [x] Qualify competing recovery on the supported local host contract: two
  independent replacement processes racing the same checkpoint produce one
  winner, one ownership failure, and no duplicate variable writes. This does
  not claim distributed fencing or failover semantics.
- [x] Prove repeated/concurrent host disposal is idempotent, releases local
  ownership, and makes later calls fail deterministically as disposed.
- [x] Qualify host publication failure before commit: the failed run records a
  terminal failure, while no capture/suspension evidence or checkpoint pointer
  is left behind.
- [x] Qualify cancellation races during active execution/publication on the
  public host path: cancellation before commit records a cancelled run without
  a checkpoint, while cancellation during a successful commit preserves the
  committed suspended boundary.
- [x] Qualify disposal during active checkpoint publication: disposal waits for
  the in-flight commit, preserves the suspended boundary, and releases local
  ownership for a replacement host.
- [x] Qualify owner process loss before checkpoint publication: a killed host
  leaves no capture/suspension evidence, checkpoint pointer, or resumable
  boundary.
- [x] Reopen production-generated loop history after host disposal with no
  executor/model/tool calls and prove passive variable playback. The existing
  schema-1.44 recovery regression continues to prove the legacy envelope and
  resume path remain unchanged.
- [ ] Only then promote the bounded loop subset; do not infer authorization from
  a structurally valid or hash-consistent frame alone.

See [loop checkpoint proof frame](0031-loop-checkpoint-proof-frame.md) and the
[checkpoint recovery guide](../checkpoint-recovery.md).
