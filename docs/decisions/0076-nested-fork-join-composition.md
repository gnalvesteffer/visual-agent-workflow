# Decision 0076: Nested Fork/Join composition

**Status:** Accepted target; public validation and authoring remain unimplemented

**Date:** 2026-11-20

## Context

Unreal Blueprint graphs can compose control flow by placing a parallel region
inside another branch. VAW currently supports static Fork/Join regions, but
Core validation and Studio reject nested regions. The private production-body
proofs now run the same static inner Fork and Join concurrently under separate
outer branches, restore the parent lineage, and cover success, failure,
cancellation, duplicate arrivals after join completion, unexpected branch IDs,
and an unrelated configured Fork identity. File and SQLite histories reopen
and passive playback preserves the results. These proofs bypass public graph
validation and do not make nested Fork/Join a supported feature.

Schema 2.0 is VAW's unified graph contract. Its purpose is to consolidate graph
capabilities without author-facing schema or capability promotion gates.
Nested Fork/Join must therefore become part of schema 2.0 rather than a new
versioned opt-in. Existing historical schemas retain their recorded behavior.

## Adjudicated direction

The independent proponent, opponent, and adjudicator review recommends
**arbitrary finite-depth nested Fork/Join as the final schema 2.0 contract**.
Do not add a fixed nesting-depth limit, schema promotion, or capability flag.
Enforce the existing general graph, activation, concurrency, event, and output
resource limits instead. An initial one-level implementation is acceptable as
a staged vertical slice, but it is not the final support boundary or a reason
to encode one-level assumptions in saved definitions.

The static validator must establish region ownership and matching joins before
publication or execution. Runtime correlation must use activation lineage, not
static Fork identity alone. Each nested region belongs to exactly one parent
branch; it rejoins there and continues within that owner. Do not share one
static nested region or continuation across sibling parent branches under this
contract. The private shared-Fork proof verifies isolated arrivals and parent
lineage restoration, but it cancels before proving that the continuation can
route to the correct outer branch-specific Join input. Supporting that shape
would require a separately reviewed parent-aware routing contract.

## Contract requirements

- Every declared Fork branch has one stable anchor and reaches exactly one
  matching Join input. Results from different Fork activations cannot mix.
- Every nested region has exactly one parent-branch owner; the child Join
  returns execution to that owner. A static child region cannot be shared
  across sibling owners.
- Nested regions cannot cross ownership boundaries, escape or bypass their
  matching Join, arrive through a wrong Join, or have ambiguous branch
  ownership. Malformed parent lineage fails closed.
- Concurrent activations of the same static Fork and Join remain independent.
  Duplicate, unknown, stale, and mismatched arrivals cannot complete a Join
  with partial or mixed values.
- Failure and cancellation settle active siblings deterministically and emit
  no joined result when any required branch does not succeed.
- Persisted events identify the activation and parent lineage sufficiently for
  file/SQLite reopening and passive playback without re-execution.
- Studio can create, edit, remove, diagnose, save, and reopen nested regions
  under the same schema 2.0 contract. Invalid drafts remain saveable but cannot
  publish or run.
- Limits remain global and explicit. Current general workflow bounds include
  256 graph nodes and bounded branch count and join-result size; implementation
  must verify that activation and recorded-event limits adequately bound
  nested concurrency and memory use.

## Implementation sequence

1. Specify and test the static ownership algorithm, including valid nested
   paths and rejection of crossing, escape, bypass, wrong-join, cycle, and
   ambiguous topologies.
2. Close runtime evidence gaps for parent-lineage tampering and duplicate
   arrivals before Join completion. Retain the existing success, failure,
   cancellation, duplicate-after-completion, unknown-branch, and mismatched
   Fork identity proofs.
3. Integrate validation with package/import handling, Studio editing, runtime,
   persistence, and passive playback without introducing a schema gate.
4. If useful, ship one nested level as an intermediate slice. Before claiming
   nested parallel parity, demonstrate deeper nesting and concurrent reuse of
   one static inner Fork under repeated activations of its same parent owner
   across the full cross-layer contract. Reject sibling-owner shared regions
   unless a separately reviewed parent-aware routing contract is introduced.

## Current status

Unified schema 2.0 now routes Join arrivals directly to VAW's
activation-correlated Join; historical schemas retain their existing fan-in
route. The focused Fork/Join runtime group passes 43/43. A malformed graph
whose Join names a different existing Fork fails in both file and SQLite
proofs, with the failure preserved by passive playback (2/2).

Public nested topology ownership, malformed parent-lineage tampering,
duplicate arrivals before completion, package/Studio authoring, and deeper
than the currently proven composition remain open. See [the Blueprint parity
roadmap](../roadmap.md) and [parallel graph behavior](../parallel-graphs.md).
