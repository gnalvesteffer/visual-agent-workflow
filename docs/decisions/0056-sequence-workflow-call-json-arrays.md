# Decision 0056: bounded typed JSON-array WorkflowCall result

## Status

Accepted and implemented in schema 1.56.

## Context

The Blueprint parity phase needs typed container values and explicit downstream
contracts, but a general array system would introduce mutation, aliasing,
unbounded fan-out, and unclear replay semantics. Schema 1.55 already proves a
bounded JSON object merge. The next safe slice adds one container type and one
deterministic consumer without turning JSON into an expression language.

## Decision

Schema 1.56 adds `array<json>` and two pure nodes:

```text
WorkflowCall.out -> core.json.to-array.in -> core.array.length.in -> Output.in
```

`core.json.to-array` accepts only a JSON array root and returns an immutable,
ordered `array<json>` value. `core.array.length` returns a signed 64-bit
integer; an empty array returns zero. The array is limited to 64 elements, JSON
depth 32, and the existing 16,384-character payload bound. Malformed JSON,
non-array roots, and limit violations fail at the refinement node.

Execution remains a direct WorkflowCall-to-Output barrier. The 1.55→1.56
promotion is schema-only and does not invent nodes or rewrite connections.
Existing 1.55 object-merge graphs remain valid under 1.56, while a graph that
contains the new array nodes cannot also contain the object merge. Studio offers
the guarded **Add array length** edit and exposes both nodes through the typed
node library.

## Rejected for this increment

- implicit JSON-array coercion or automatic array flattening;
- mutable/shared array state or element-level pins;
- arbitrary array operators, fan-out, loops, or data-driven execution routing;
- mixing array refinement with the bounded object merge;
- treating visual layout or topological order as runtime scheduling.

## Evidence

Core tests cover schema catalog/promotion/migration inventory, descriptor
conformance, valid and invalid topology, and schema-only identity preservation.
Agent Framework tests execute a pinned JSON child returning an array, record
both pure data evaluations, and produce the integer count at Output.
