# Decision 0029: loop-variable evidence seam

**Status:** Evidence seam shipped; loop-variable execution is bounded by schema 1.45 and explicit checkpoint admission.

**Date:** 2026-10-28

## Context

Schema 1.44 deliberately admits only an acyclic linear variable graph. The
existing bounded loop has an internal iteration counter, but variable evidence
did not carry that identity. A future Blueprint-style loop-variable contract
would otherwise be difficult to replay or distinguish when the same `Get` or
`Set` node activates repeatedly.

The adversarial review of the next step recommended a proof increment:
preserve schema 1.44, add observable iteration identity, and defer schema 1.45
until ordering, cancellation, failure, playback, and loop-aware checkpoint
proofs are complete.

## Decision

Variable read/write evidence now carries an additive `loopIteration` integer.
The current linear schema emits `0`; passive playback accepts and preserves
non-negative future iteration values. Older evidence without the property
continues to parse as iteration `0`.

This remains evidence metadata for schema 1.44 and older graphs; schema 1.45
defines the bounded loop-variable execution contract. It does not grant loop
checkpoint recovery to the default host: recovery requires the explicit host
option and the composite checkpoint cursor/frame contract.

## Evidence

- `VariableStateEvidence` validates and exposes the optional iteration identity.
- `VariablePlaybackOperation` preserves it during prefix reconstruction.
- Runtime variable events emit the current private graph-message iteration
  counter, which is zero for all currently admitted variable graphs.
- Core tests cover future iteration parsing and negative-value rejection; the
  Agent Framework regression confirms current linear events remain at zero and
  cancellation or recorder failure after a committed write preserves the
  recorded prefix, and Core playback tests reconstruct every valid prefix of a
  repeated, iteration-tagged operation sequence without execution.

See [the mutable-variable proof gate](0027-run-scoped-variable-proof-gate.md)
and [the variable checkpoint frame](0028-variable-checkpoint-frame.md).
