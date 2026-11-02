# Decision 0066: Unified typed array values

**Status:** Immutable typed-array composition and workflow input/output boundaries implemented in schema 2.0; parameters and pure functions remain deferred
**Date:** 2026-11-02  
**Scope:** Blueprint parity workstream; current authoring schema 2.0

## Context

Blueprint arrays are ordinary typed graph values. VAW currently has a bounded
`array<json>` value, but schema 2.0 admits it only through exact caller-input
count and indexed-selection topologies. Those contracts prove useful bounds and
recorded evaluation, but not general graph composition. The type catalog also
requires exact pin identity and has no typed collection family.

The Blueprint parity capability matrix identifies containers as a major
language gap. Mutable variables, events, async work, reuse, and debugging also
remain incomplete. An independent proponent/opponent/adjudicator review
selected immutable typed arrays as the next parity workstream, while rejecting
an all-at-once delivery across every collection kind and graph placement. The
review reports unknown demand for maps/sets and unknown portability needs across
external consumers.

## Decision

Build collection support as a staged expansion of current schema 2.0. Do not add
feature-specific schema gates, promotion buttons, or preview modes. Historical
1.x definitions, hashes, execution behavior, and recorded playback keep their
existing contracts.

The first contract is an immutable, ordered, homogeneous `array<T>` where `T`
is one of `string`, `boolean`, `integer`, or finite `number`. Existing
`array<json>` keeps its heterogeneous JSON and JSON-null semantics. The new
primitive arrays are distinct types: they do not implicitly convert to or from
`array<json>` or another element type. Null is not a member of a primitive
array; nested arrays, maps, sets, records, and reference-like values are not
part of this slice. Empty arrays are valid when their element type is declared.

The initial compositional graph vocabulary is:

- **Array literal:** a pure value node with a declared element type and a
  bounded JSON array of literal values. Validation requires each item to match
  the declared primitive type; numbers must be finite. The node's output pin is
  exactly `array<T>`.
- **Array length:** accepts a configured `array<T>` and returns a signed
  64-bit integer.
- **Array get:** accepts a configured `array<T>` and signed 64-bit index, and
  returns exactly `T`. Indices are zero-based and do not wrap. Negative or
  out-of-range indices fail at this node; JSON null remains supported only by
  the existing `array<json>` behavior.

These nodes compose in ordinary schema-2.0 explicit graphs with existing pure
scalar nodes and compatible downstream data inputs. Decision 0067 separately
extends the same bounded types across workflow input/output boundaries using
JSON text on the existing string API. Typed parameters and graph-function
ports, variables, workflow-interface fields, MCP arguments, Sequence paths,
loop-carried state, parallel shared state, and checkpoint state remain
unsupported. Each needs its own contract and verification. The existing
`JsonToArray` path continues to produce `array<json>`.

The implementation reuses the 64-item collection bound and adds one explicit
encoded-value bound no larger than the current JSON graph payload limit. Type identity is exact: `array<T>` connects only to the same
`array<T>`. Array order is semantic. Once equality or membership nodes exist,
array equality is structural and order-sensitive; strings compare ordinally,
Booleans and integers by value, and finite numbers by numeric value. This slice
does not add equality or membership operations.

Pure evaluation remains lazy and deterministic. Literal, length, and selected
item values use the existing bounded data-evaluation evidence and passive
playback path; replay never parses a live workflow input or re-evaluates the
array. Failure evidence is attributed to the node that rejects the value or
index. Future iteration must define deterministic ascending-index order,
sequential activation, a hard visit bound, cancellation between elements, and
per-element evidence before an iteration node is admitted. Iteration is not
part of this implementation.

Maps and sets remain a later design decision. They require demonstrated
portable key/equality/order rules. Mutable collection variables and checkpoint
recovery remain separate from immutable collection values and require state
snapshot, mutation ordering, cancellation, and recovery proofs.

## Compatibility

The current schema remains 2.0. New optional node configuration and the new
`array<T>` type identities are additive current-schema contracts. Existing
schema-2.0 JSON-array nodes retain their existing `array<json>` behavior;
historical schema-1.x adapters and execution-shape selection are unchanged.
Definitions are never silently rewritten on load. Any required old-graph
normalization remains explicit, deterministic, and provenance-preserving.

## Verification for the first shipped slice

- Core tests cover type parsing/identity, exact compatibility, valid empty and
  populated literals, wrong element types, nulls, non-finite numbers, item and
  encoded-value bounds, and stable serialization.
- Core graph validation admits ordinary schema-2.0 compositions and rejects
  incompatible array types and unsupported placements without blocking draft
  saving.
- Runtime tests cover all four primitive types through literal → get → scalar
  output and literal → length/get → scalar composition, plus index failures,
  node attribution, and data-evaluation evidence. The shared playback path reads
  these recorded events without evaluating nodes again.
- Checkpoint admission continues to reject unsupported array-bearing shapes.
- Historical 1.x fixtures and the existing JSON-array sequences preserve hashes,
  behavior, and playback.
- Studio authoring exposes the element type and literal values, updates typed
  pins from one canonical contract, and saves invalid drafts for correction.
- Architecture, user guide, capability matrix, and roadmap distinguish shipped
  behavior from placements still deferred.

## Review record

The required independent proponent, opponent, and adjudicator roles
participated. The adjudicator recommended revising the broad collection
proposal into this staged typed-array direction. The review did not establish
user demand, maps/sets portability, or support for typed arrays at every
consumer boundary.
