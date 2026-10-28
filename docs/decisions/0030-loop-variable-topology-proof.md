# Decision 0030: loop-variable topology proof seam

**Status:** Bounded schema 1.45 shipped; checkpoint admission and broader topology remain deferred.

**Date:** 2026-10-28

## Context

The shipped schema 1.44 mutable-variable graph is intentionally acyclic and
linear. Schema 1.45 adds one explicit bounded loop around one linear body, so
mutable writes and reads can repeat with an explicit iteration identity. It
does not broaden 1.44 or admit checkpoint recovery for loop state.

## Decision

Core now contains a topology analyzer and authoritative schema 1.45 validator
for one loop, one body anchor, one linear body, explicit execution edges,
scalar variable Get/Set references, bounded body size, and no unsupported
control or authority-bearing nodes. The runtime routes `LoopBody.then` through
the typed explicit graph and carries the loop iteration through variable
evidence and passive playback. Schema 1.44 remains unchanged.

Schema 1.45 intentionally does not enable Studio promotion/editing or
checkpoint recovery. Checkpoint admission, repeated recovery, and broader
loop/parallel/Sequence/shared-child combinations require a separate contract.

## Evidence

- `LoopVariableTopologyAnalyzer` accepts a complete schema 1.45 shape and emits
  stable diagnostics for a non-linear body.
- Core validation tests reject malformed loop-variable topology while accepting
  the bounded candidate.
- Agent Framework tests prove three bounded passes, repeated Set/Get evidence,
  and the maximum-iteration stop reason.
- Existing cancellation, failure, and playback-prefix proofs remain additive;
  loop-aware checkpoint recovery is still not admitted.

See [the mutable-variable proof gate](0027-run-scoped-variable-proof-gate.md),
[the loop-variable evidence seam](0029-loop-variable-evidence-seam.md), and
[the bounded loop contract](../loop-graphs.md).
