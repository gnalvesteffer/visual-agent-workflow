# Decision 0072: Primitive array membership

**Status: Accepted and implemented in unified schema 2.0.**

## Context

The Unreal Blueprint capability audit identifies array search as a core
collection operation. VAW already has bounded immutable homogeneous arrays and
typed primitive values, but no membership node. Existing contracts define
strings as ordinal text, integers as signed 64-bit values, and numbers as finite
IEEE-754 doubles. JSON-valued array equality remains intentionally undefined.

## Adjudicated scope

Three independent roles reviewed the proposed collection increment. The
proponent recommended primitive membership and ordered array equality. The
opponent identified unresolved cross-type and JSON structural-equality risks.
The adjudicator revised the scope to **Array Contains only**, contingent on one
shared scalar equality rule. Array Equals and `array<json>` membership remain
deferred until they have separately justified contracts.

## Contract

- `core.array.contains` is a pure, lazy data node with dynamic pins determined
  by `ArrayElementType`: `array<T>` and `T` inputs, and a Boolean `out` value.
- Supported `T` values are `string`, `boolean`, `integer`, and `number`.
  `array<json>` is rejected because this contract does not define structural
  JSON equality.
- Equality is shared with existing string and integer comparison nodes:
  strings use ordinal comparison, booleans compare by value, signed 64-bit
  integers compare by value, and finite IEEE-754 doubles compare numerically.
  Positive and negative zero compare equal; decimal spellings that normalize to
  the same double compare equal.
- Search is deterministic and bounded by the existing 64-item array limit.
  Empty arrays return false; duplicates do not change the result. The node
  produces ordinary data-evaluation evidence and works in main graphs and pure
  graph functions.
- No new schema identity, promotion step, or preview mode is introduced.

## Verification

- Core tests cover exact dynamic pins, JSON exclusion, ordinal strings, Boolean
  and Int64 equality, finite-number equality, signed zero, and nearby decimal
  values.
- Agent Framework tests cover all four primitive types, positive and negative
  membership, empty arrays, recorded Boolean evidence, and the last item in a
  maximum-size array. A pure graph-function test covers evaluation and rejects
  JSON membership.
- On 2026-11-17, Core passed 635/635 tests and focused Array Contains Agent
  Framework tests passed 7/7. The full Agent Framework suite was not completed:
  it reported a nested-Break concurrency failure
  (`ConcurrentProductionBodyCoordinator_NestedBreakTargetsNearestFrame`,
  expected 2 and observed 3) and then stalled. This belongs to the separate
  Sequence/Break workstream.
- The broad `graph_canvas_smoke.py` stopped earlier at its node-comment
  expansion assertion. A focused Playwright browser check then added Array
  Contains through the node library and observed no graph issues.

## Consequences

Authors can search primitive arrays in current-schema workflows and pure graph
functions. Arrays of arbitrary JSON values retain their existing operations
without receiving an implicit or guessed equality policy. Array equality,
maps, sets, and the reviewed array iteration/Break runtime gate remain separate
parity work.
