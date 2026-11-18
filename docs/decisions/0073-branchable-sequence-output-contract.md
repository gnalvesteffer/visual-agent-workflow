# Decision 0073: Branchable Sequence output contract

**Status:** Design direction accepted; route-to-output settlement proof required
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

If the output-to-route mapping requires a second lifecycle/collector owner or
cannot preserve these invariants, return the design for review rather than
weakening the graph contract. No production behavior is changed by this
decision.
