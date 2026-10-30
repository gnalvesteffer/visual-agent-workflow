# Decision 0055: bounded WorkflowCall JSON multi-consumer merge

Status: accepted

## Context

The Blueprint parity roadmap needs a concrete multi-consumer/dataflow proof after
the schema 1.54 JSON chain. A general merge node would introduce precedence,
recursive semantics, effect ordering, and replay questions that are larger than
this increment.

## Decision

Schema 1.55 admits one pure `core.json.merge` node in the exact topology:

```text
WorkflowCall.out ──┬──> JsonProject.in ──> JsonMerge.right
                   └──> JsonMerge.left ──> JsonMerge.out ──> Output.in
WorkflowCall execution ────────────────────────────────────> Output execution
```

Both merge inputs must be JSON objects. The result is a shallow union in ordinal
property-name order. Duplicate decoded top-level names fail deterministically;
nested objects are copied as values and are not recursively merged. The node is
bounded to 32 properties, the existing 32 JSON depth limit, and the existing
payload character limit. No arrays, scalars, effectful nodes, arbitrary fan-out,
additional merge nodes, or merge chains are admitted.

The child output is shared by the two data consumers but remains behind the
existing WorkflowCall execution barrier. Runtime evaluation caches the child
value within one input-resolution boundary and records each connection-correlated
data evaluation. Schema 1.54 remains valid under schema 1.55; explicit promotion
is schema-only, while Studio’s guarded authoring action adds the merge topology.

## Rejected alternatives

- General recursive merge: precedence and depth semantics are not yet a stable
  Blueprint-compatible contract.
- Implicit last-writer-wins behavior: it hides graph mistakes and is not safe for
  deterministic evidence or replay.
- Automatic migration that invents a merge node: migrations preserve graph
  identity and must not change authored dataflow.

## Consequences

The increment proves typed fan-out and fan-in with explicit validation, runtime,
evidence, package, and authoring boundaries. Broader typed node families and
multi-consumer contracts remain a separate roadmap item.
