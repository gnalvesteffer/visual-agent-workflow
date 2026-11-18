# Decision 0073: Branchable Sequence output contract

**Status:** Bounded Condition/Boolean Branch → Merge shape admitted in unified schema 2.0; broader Sequence/Break iterator gate remains open
**Date:** 2026-11-17

## Context

Unified schema 2.0 currently restricts each Sequence output to a bounded,
disjoint linear path ending at the workflow Output. An attempted conditional
route inside an output path was rejected with `sequence.path.node`,
`sequence.path.terminal`, `sequence.path.unreachable`, and
`sequence.path.barrier`. This blocks a Sequence sibling from continuing through
ordinary work while another activation selects Break.

The current production plan starts and settles work per Sequence output. Its
route graph is therefore more expressive than its settlement model. Merely
allowing Condition or Branch nodes through validation would risk missing or
duplicate output settlement, incorrect Break scope, and evidence that cannot
be reconstructed in playback.

## Adjudicated direction

The three-role review returned **REVISE**. Prefer a bounded decision region
within each Sequence output, composed from existing Condition,
BooleanBranch, and ExclusiveMerge contracts. Do not add a Sequence-specific
conditional node. First prove an explicit mapping from a selected route to
exactly one Sequence output activation; do not implement or claim branchable
Sequence behavior until that proof and its acceptance matrix pass.

The design remains in unified schema 2.0. It introduces no feature schema,
promotion step, preview mode, or execution ordering derived from layout. This
decision does not select the array-iteration runtime architecture and does not
add a public Break node.

## Required contract proof

Before implementation, prove and document:

- One Sequence output owns one bounded, disjoint decision region. Existing
  linear paths remain valid. Cycles, unreachable nodes, cross-output sharing,
  ambiguous terminal routes, and multiple settlement are rejected with stable
  diagnostics.
- A condition or Boolean Branch selects one route. An unselected route does
  not execute or generate node/tool/settlement evidence. An ExclusiveMerge
  accepts the selected alternative once and remains distinct from a parallel
  join.
- Every selected route maps to one output activation and settles it exactly
  once. Define which route terminals represent normal completion and successful
  Break; Break requests target the nearest owning activation and do not count as
  workflow Output results.
- Failure, outcome-unknown, and cancellation cannot be hidden by a later
  successful merge or completion. Define admitted-sibling draining and the
  precedence of Break against failure, unknown external effects, and
  cancellation.
- Sequence invocation, output activation, execution connection, selected
  branch, merge arrival, Break source/target, and final settlement retain stable
  identity in event evidence and passive playback.
- File and SQLite reopen reconstruct the same event prefix and settlement.
  Existing linear unified-schema Sequence graphs preserve serialization,
  execution, and playback behavior.

## Falsifiable cases

The proof gate remains open until it demonstrates all of these through the
production body and Sequence path:

1. A normal branch and a successful Break branch each settle one output once;
   the unselected arm is absent from recorded execution.
2. A selected arm can pass through an ExclusiveMerge once; a parallel barrier
   is never treated as that exclusive merge.
3. A sibling activation continues its own Sequence while another activation
   breaks. The broken frame admits no later item, admitted work drains, and an
   enclosing frame can continue.
4. Deterministic failure, unknown MCP outcome, and host cancellation at the
   relevant races preserve their established run outcomes and do not fabricate
   successful output settlement.
5. Invalid, cyclic, unreachable, shared, ambiguous, and multiply settled
   regions fail closed; valid graphs round-trip under schema 2.0 and retain
   stable node, port, and connection identities.
6. File and SQLite histories reopen identically and passive playback shows the
   route and settlement without re-execution.

## Proof progress (2026-11-17)

The private production-runner proof now dispatches one Sequence output through
both inputs of one Merge: one arm connects directly, and the other passes
through a deterministic text node. Either selection reaches Merge exactly once;
the other arm has no transfer or node-start evidence. A downstream decision
then selects normal completion or a private successful Break route. The normal
route completes the next Sequence output; Break uses Output's special `break`
handoff, completes the active output once, and skips the suffix. Both routes
reach the same collector. The selected branch events carry the same Sequence
invocation, output ID, ordinal, activation ID, and originating connection
identity as their execution-transfer events. In-memory passive playback
reconstructs both `completed` and `break-completed` positions. File and SQLite
reopen preserve the exact event prefix, and passive playback reconstructs the
same states from each reopened store (4/4 store/outcome cases). Focused
production-runner coverage passed both alternatives (2/2).

This exposed and fixed three evidence/runtime gaps: branch-decision events had
omitted Sequence lineage, the production Sequence builder did not route the
special Output Break handoff into its collector, and explicit-graph Merge and
Approval executors emitted `out` despite exposing `then` pins. These are
internal proof changes plus a correction to explicit execution-pin dispatch.
They do not admit conditional Sequence definitions in the public validator.
The proof-only production runner now also routes the selected branch through a
controlled MCP node after the exclusive merge. Fatal tool failure and an
explicitly unknown tool outcome each settle the selected Sequence output as
failed exactly once, skip the later suffix, and suppress the downstream
completion/Break decision. Both branch choices and both outcomes reopen with
identical file and SQLite event prefixes and failed passive playback (8/8).
Host cancellation is now also injected after the selected branch has reached a
controlled Agent beyond the merge. The active output settles cancelled, the
later suffix is skipped, no downstream completion/Break node starts, and file
and SQLite playback reopen identically for both branch choices (4/4). This is
deterministic executor evidence, not a live provider cancellation guarantee.
The existing linear unified-schema compatibility path is also verified. A Core
package/JSON round trip preserves ordered output IDs, execution connections,
and definition hash (SequenceGraphTests 30/30). Production execution tests for
typed direct-MCP arguments and Agent-to-transform paths now reconstruct the
same completed Sequence playback (2/2). This covers the existing linear
contract; malformed-region validation and round trips for the future branched
contract remain open.

The public validator remains linear-only. Core characterization tests now
build a structurally valid one-branch/one-merge unified candidate and confirm
that it is still rejected at `sequence.path.node`, while graph-level cycle,
unreachable-node, and duplicate-branch-settlement mutations are rejected with
stable `graph.cycle`, `graph.unreachable`, and `graph.degree` diagnostics
(4/4). This demonstrates fail-closed admission for these malformed shapes; it
does not implement the branched-region validator. A proof-only Core topology
analyzer now recognizes one bounded Condition or Boolean Branch with two
exclusive arms, one matching Merge, and a linear suffix. Its tests confirm both
decision forms, permit a disjoint linear sibling output, and report cross-output
node sharing and an arm that terminates before the Merge. Draft JSON round-trip
also preserves the candidate bytes and route identities. The analyzer is not
wired into public admission. Existing graph
validation tests cover cycles, unreachable nodes, and duplicate settlement;
branch-specific tests cover cross-output sharing and ambiguous terminals.
Valid package serialization and admission of a branched definition remain
open.

If the output-to-route mapping requires a second lifecycle/collector owner or
cannot preserve these invariants, return the design for review rather than
weakening the graph contract. This decision does not itself admit branchable
Sequence graphs in public validation or Studio authoring.

## Public admission update (2026-11-18)

The bounded single-decision region is now admitted by the unified schema 2.0
validator. Public definitions use the existing Condition, BooleanBranch, and
Merge nodes; no schema promotion or special Sequence node was added. Validation
composes explicit-edge checks with the topology analyzer and rejects shared
routes, incomplete or ambiguous regions, and Boolean Branch arms that cannot
identify a selected Merge data input. String Condition and Boolean Branch
forms both pass Core validation and package JSON round-trip coverage. Public
runner tests exercise both choices for each decision form and confirm the
unselected arm does not start and the Merge starts once. Focused Core tests
passed 40/40 and runner cases passed 4/4.

This admission closes the public bounded branch topology item, not the broader
concurrent Sequence/Break iterator acceptance gate above. Failure, unknown
external effects, cancellation, persistence, and passive playback continue to
be covered by the production-runner proof matrix; the separate integrated
array-iteration architecture and its acceptance gate remain open. Historical
schema behavior is unchanged.

## Composed activation completion review (2026-11-18)

A three-role review considered how Sequence completion could compose with an
enclosing activation region. The adjudicator returned **REVISE**: reject
topology-only completion inference, and do not add a separate public barrier
node based on the current evidence. The only proof candidate is a typed
aggregate completion signal owned by the existing Sequence collector, distinct
from each output activation's settlement and carrying the Sequence/frame and
activation lineage. This is not an approved public contract or runtime change.

A follow-up three-role review returned **REVISE** again. Treat the aggregate
signal as a testable provisional contract, while leaving its outcome semantics
explicitly unresolved. Keep the existing exactly-once local output settlement
and terminal categories; do not invent precedence among conflicting failure,
unknown external effect, cancellation, success, or Break outcomes. Do not assume
that admitted siblings drain, or that one activation's Break cannot affect
another, until proved. Break-vs-next-admission ordering needs a demonstrated
collector serialization point. Persistence rejection must not produce durable
aggregate completion or trigger a duplicate retry without idempotency evidence.

The next proof must race Break against next-item admission while another
activation remains unsettled; exercise sibling failure, unknown outcome, and
cancellation; inject persistence rejection before and after simulated durable
commit; and verify the selected terminal or explicit unresolved disposition.
It must also prove that playback handles a final output settlement with no
aggregate event, and that file and SQLite histories reconstruct the same
accepted prefix. If these cases cannot establish deterministic ownership,
settlement, and replay semantics, retain this as an unresolved gate. No
author-facing schema/capability gate is introduced, and legacy identity-
preserving migration remains required.

Existing coordinator tests already cover atomic Break/admission serialization
and characterize failure cancellation of other active outputs. These tests do
not settle the new aggregate contract, mixed-outcome precedence, persistence
rejection, or playback with a missing aggregate event.

### Proof progress (2026-11-18)

Core passive-playback coverage now includes a complete output settlement with
no Sequence terminal event. The reader reconstructs the accepted prefix as
`running` and retains the output activation identity, rather than inferring
Sequence completion. The focused regression passed (1/1). This establishes
prefix behavior only; it does not qualify file/SQLite persistence rejection or
the aggregate signal contract.

File and SQLite store regressions now inject a rejected settlement append both
before commit and after a simulated commit with a lost acknowledgement (4/4).
After reopen, playback preserves `dispatched` for the uncommitted case and
`completed` for the accepted case, while keeping the Sequence itself `running`
because no aggregate terminal was accepted. This proves the reader does not
invent completion across these append outcomes; it does not qualify production
runtime retry behavior or mixed sibling outcomes.
