# Decision 0030: loop-variable topology proof seam

**Status:** Proof-only analyzer shipped; schema admission remains deferred.

**Date:** 2026-10-28

## Context

The shipped schema 1.44 mutable-variable graph is intentionally acyclic and
linear. Existing bounded loops use legacy data-wire scheduling, while mutable
variables require explicit execution/data separation. Admitting both by simply
relaxing the 1.44 validator would change a published contract without proving
ordering, cancellation, failure, or recovery semantics.

## Decision

Core now contains a non-authoritative `LoopVariableTopologyAnalyzer` for the
candidate future shape. It checks one loop, one body anchor, one linear body,
explicit execution edges, scalar variable Get/Set references, bounded body
size, and the absence of unsupported control or authority-bearing nodes.

The analyzer is a proof seam only. It does not add schema 1.45, alter the
validator, create a runtime plan, enable Studio authoring, or admit checkpoint
recovery. The eventual promotion must independently prove runtime ordering,
iteration evidence, cancellation and failure boundaries, passive playback, and
loop-aware checkpoint state.

## Evidence

- `LoopVariableTopologyAnalyzer` accepts a complete candidate shape and emits
  stable diagnostics for a non-linear body.
- Core tests confirm the candidate is still rejected by the authoritative
  schema 1.44 validator.
- Existing variable evidence, cancellation, failure, and playback-prefix
  proofs remain additive prerequisites rather than implicit admission.

See [the mutable-variable proof gate](0027-run-scoped-variable-proof-gate.md),
[the loop-variable evidence seam](0029-loop-variable-evidence-seam.md), and
[the bounded loop contract](../loop-graphs.md).
