# Decision 0061: Unified JSON-array input chain

**Status:** Accepted and implemented (2026-11-02)

## Decision

Schema 2.0 supports the existing bounded `array<json>` value in one direct
caller-input path:

`Input(JSON) → JsonToArray → ArrayLength → Output(Integer)`

The Input execution pin activates Output directly. Output resolves the typed
data chain lazily and records its normal data-evaluation evidence. The graph
must contain exactly these four nodes and three data edges. The JSON array is
limited to 64 items and depth 32. Non-array JSON and values outside the bound
fail at the `JsonToArray` evaluation and are attributed to that node.

The Studio node library exposes both nodes in schema 2.0. Authors can add
incomplete drafts as usual; validation blocks publish and execution until the
accepted topology is complete. No schema promotion is needed.

Historical 1.x definitions continue through their existing validators,
execution-shape selection, runtime behavior, and playback. This decision does
not admit the array nodes in Sequence regions, graph functions, arbitrary
effectful graphs, or checkpoint recovery. Those placements need their own
ordering, value-boundary, evidence, and recovery proofs.

## Rationale

The existing 1.56 array feature was tied to a pinned WorkflowCall result. A
schema 2.0 author can now count a JSON array supplied directly by the caller,
without creating a child workflow. The first contract stays narrow so its
lazy evaluation order and failure evidence remain explicit. It does not alter
legacy schema identity or reinterpret historical records.

## Verification contract

- Core accepts the exact unified topology and rejects incorrect output types,
  extra nodes, extra data connections, and incorrect activation wiring.
- Runtime returns counts for empty and heterogeneous arrays, records the
  `JsonToArray` and `ArrayLength` evaluations, and fails with node-attributed
  evidence for non-array input or more than 64 items.
- Existing schema 1.56 topology and production-runner tests remain unchanged.
- Checkpoint and graph-function eligibility are not expanded by this slice.
