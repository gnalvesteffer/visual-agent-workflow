# Decision 0065: Unified JSON-array element selection

**Status:** Accepted and implemented (2026-11-02)

## Decision

Current schema 2.0 adds one exact `ArrayGet` use of the bounded `array<json>` value:

```text
Input(JSON).out → JsonToArray.in
IntegerConstant.out → ArrayGet.index
JsonToArray.out → ArrayGet.array
ArrayGet.item → Output(JSON).in
Input.then → Output.execute
```

The five-node topology is the entire supported graph for this slice. It has no
extra nodes, data consumers, or execution connections. The index is signed
64-bit, zero-based, and never wraps. A negative or out-of-range index, including
any index into an empty array, fails at `ArrayGet` and produces node-attributed
failure evidence. A valid element is cloned as JSON without stringification or
coercion; `null` remains JSON null. The input keeps the existing 64-item,
depth-32, and bounded-payload limits, and JSON Output applies its existing
output bounds.

This is an ordinary capability in schema 2.0, with no feature-specific schema,
promotion, or preview mode. Existing historical 1.x definitions and recorded
revisions keep their original validation, execution, and playback contracts.
This decision does not add arrays to checkpoint recovery, graph functions,
Sequence regions, or general graph compositions.

## Rationale

Array length already proves bounded lazy evaluation of `array<json>` from caller
input. Element selection is a narrow next operation that makes the container
useful while preserving the JSON value contract. Restricting the topology keeps
the runtime, payload evidence, and failure boundary explicit; broader
composition needs separate proof.

## Verification contract

- Core accepts the exact five-node topology and rejects extra consumers and
  unsupported topology.
- Runtime returns first, middle, last, object, and JSON-null values unchanged.
- Negative indices, high indices, and every index into an empty array fail at
  `ArrayGet` with recorded node attribution.
- Existing `JsonToArray` item/depth bounds and JSON output bounds still apply.
- Data-evaluation evidence records `ArrayGet.item` with type `json` and the
  selected value; playback reconstructs it from recorded events without
  reevaluation.
- Checkpoint eligibility remains unchanged.
