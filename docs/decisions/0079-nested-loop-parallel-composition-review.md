# Decision 0079: Defer nested Loop and Fork/Join composition

**Status:** Reviewed; composition remains unsupported pending a production proof.

**Date:** 2026-11-23

## Context

Unified schema 2.0 now supports nested bounded Loops and a single Loop body can
contain one complete static Fork/Join region. Neither contract proves that
nested Loop activation frames and parallel branch/Join lineage compose safely.
The Blueprint parity roadmap should keep advancing without implying support
for unproven cross-compositions.

## Review outcome

A proponent, opponent, and adjudicator independently reviewed whether to admit
Fork/Join inside nested Loops, Loops inside Fork branches, or both. The
adjudicator returned **REVISE**: keep the current rejection and pursue another
parity item. The three-role review is complete; it did not authorize a public
contract change.

The two existing capabilities have separate ordinary-runner tests, but current
evidence does not establish their combined activation ownership or settlement.
The unresolved cases include stale or duplicate Join arrivals across Loop
passes, branch failure or cancellation while sibling work remains active,
shared budget reservations across concurrent branches, and how those states
are represented in persisted playback. The existing Join delivery-finished
callback does not account for every active descendant that has not yet sent a
message to the Join. Root-wide Loop budgets are now shared across pinned
WorkflowCall descendants under Decision 0078; that behavior is covered by
focused ordinary-runner unit tests.

## Future proof boundary

Before changing the public contract, a new proposal should prove one inner
bounded Loop inside a branch of the existing single-Loop-owned Fork/Join region
over repeated outer passes. Core validation and ordinary Agent Framework unit
tests must cover ownership and feedback paths; successful Join/Loop settlement;
inner failure and cancellation; stale and duplicate arrivals; atomic shared
root-run work/evidence limits including pinned WorkflowCall descendants;
and persisted event-prefix playback where runtime evidence changes.

Until that proof is independently reviewed, reject both Fork/Join inside
nested Loop bodies and Loops inside Fork branches. This decision does not
authorize typed-array iteration or public Break authoring, which remain gated
separately by [Decision 0070](0070-bounded-array-iteration-contract.md).

See [single-Loop Fork/Join composition](0077-loop-parallel-composition.md),
[nested bounded Loops](0078-nested-bounded-loops.md),
[bounded Loop graphs](../loop-graphs.md), and the
[Blueprint capability matrix](../blueprint-capability-matrix.md).
