# Decision 0076: Nested Fork/Join composition

**Status:** Accepted target; static schema-2.0 ownership validation, public success/failure/cancellation proofs, Studio insertion/removal and existing-region branch-count editing, versioned activation/arrival playback, File/SQLite reopen, and three-level workflow/project-package round trips are implemented; runtime duplicate-arrival races remain open

**Date:** 2026-11-20

## Context

Unreal Blueprint graphs can compose control flow by placing a parallel region
inside another branch. VAW supports static Fork/Join regions. Core now validates
strictly owned nested regions and the public runner has a three-level success
proof. Studio can insert a two-branch execution skeleton on an eligible explicit
execution edge, remove the smallest containing region, and edit the branch count
of an existing region while preserving stable branch IDs. Earlier private
production-body proofs ran the same static inner Fork and Join concurrently
under separate outer branches, restored parent lineage, and covered success,
failure, cancellation, duplicate arrivals after join completion, unexpected
branch IDs, and an unrelated configured Fork identity. Their file/SQLite
reopen and playback checks bypassed public graph validation; the cross-layer
proofs and remaining gaps are tracked below.

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

Core now validates nested unified-schema Fork/Join regions with arbitrary
finite depth, requires a unique parent-branch owner, and rejects shared
sibling ownership and joins paired with another Fork. Unified validation
also supports sequential root regions. Regressions cover two and three nested
levels, sequential roots, and malformed ownership/pairing cases. The
public runner also executes a normally validated graph with three nested levels
and a later sequential root region to a successful joined result. Public-runner
failure and cancellation regressions now verify that no nested Join or final
Output reports a completed result when a required branch does not succeed.
Studio resolves a selected node to the smallest containing nested Fork/Join
region. Its removal preview identifies the matching Join and all removed nodes;
removal deletes contained descendants, preserves the parent region, and
reconnects the owner branch to the removed region's continuation. Surviving
data consumers are listed and remain in a saveable invalid draft when their
inputs are disconnected. Studio insertion is one undoable edit and preserves
all data connections; authors wire branch data separately. A public runner
regression executes a nested region inserted inside an existing owned branch.
Core tests cover selecting/removing nested and deep regions, parent
preservation, continuation routing, and root removal without schema/output-type
changes. Schema 2.0 now emits versioned `parallel.join.arrival` events after
accepting a branch result. The payload identifies the Join, Fork activation,
branch, and complete root-to-leaf Fork lineage. Core validates configured
Fork/Join/branch identities and the static nested-owner chain, then passively
reconstructs accepted branch counts and nesting depth at any recorded prefix.
Schema 2.0 also records a versioned `parallel.join.activated` event immediately
before dispatching branch work. It means the Fork activation was admitted for
dispatch; it does not claim every branch started or reached the Join. The event
contains the paired Fork/Join IDs, activation ID, expected branch IDs, and
parent lineage. If recording fails, branch dispatch does not begin. Arrival
events remain the sole evidence that a branch result was accepted. Playback
validates activation and arrival identities against the saved graph and keeps
Join lifecycle state separate from the run outcome. A zero-arrival activation
left open at failure/cancellation is shown as incomplete with 0 of the expected
branches, rather than falsely assigning the run's cause to the Join. The run
inspector renders this projection without re-executing the workflow.

Histories without activation events retain the prior accepted-arrival
projection; histories with neither event retain their ordinary status and
progress, and the inspector explicitly reports missing lineage instead of
reconstructing it from progress text.
A three-level public-runner regression verifies eight arrivals, successful
reconstruction, rejection of tampered parent lineage, and rejection of duplicate
arrival evidence. Public failure/cancellation regressions reconstruct zero and
partial arrivals as incomplete while preserving the run terminal status. A
rendered run-inspector proof shows a zero-arrival activation and its accepted
count at a selected prefix. File and SQLite reopen tests cover zero and partial
arrivals for both failed and cancelled runs, without re-execution. A current
schema 2.0 graph with three nested Fork levels also round-trips through a
workflow package and a project release while preserving definition/layout
hashes and exact serialized bytes. Studio's existing-region editor can add and
reorder branches and remove empty branches by resolving the selected Fork's
uniquely matching Join. Core and browser regressions prove an inner branch-count
edit preserves its containing outer Fork/Join region, existing wires, stable
branch identities, and graph validity. Removal is available only when the
matching Join is unique. Runtime duplicate-arrival race coverage remains open.
These tests
establish structural consistency and replay, not cryptographic tamper
resistance. See [the
Blueprint parity roadmap](../roadmap.md) and
[parallel graph behavior](../parallel-graphs.md).
