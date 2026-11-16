# Decision 0066: Unified typed array values

**Status:** Primitive typed arrays compose across boundaries, parameters, graph functions, and variables in schema 2.0. JSON-valued arrays compose through bounded array operations and typed workflow parameters; JSON-valued graph-function ports remain separate.
**Date:** 2026-11-02  
**Scope:** Blueprint parity workstream; current authoring schema 2.0

## Context

Blueprint arrays are ordinary typed graph values. VAW has bounded primitive
array types and a heterogeneous `array<json>` value. Primitive arrays compose
through graph values, parameters, graph functions, and variables; JSON arrays
compose through bounded array operations and workflow parameters. Other
placements retain separate contracts and verification requirements.

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

The primitive collection contract is an immutable, ordered, homogeneous
`array<T>` where `T` is one of `string`, `boolean`, `integer`, or finite
`number`. Those primitive arrays do not implicitly convert to `array<json>` or
another element type. Empty arrays are valid when their element type is
declared. A separate follow-up now composes the existing heterogeneous
`array<json>` identity without changing its null semantics or inventing a
competing type.

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
scalar nodes and compatible downstream data inputs. JSON-valued arrays now use
the same literal, length, indexed-read, append, insert, replace, remove, and
concatenate nodes, preserving heterogeneous JSON values, nested arrays, null,
object member order, and number-token spelling. JSON values are copied with
`JsonElement.WriteTo`; string escaping may normalize during compact
serialization. JSON array literals accept at most 64 outer items, JSON nesting
depth 32 (root counts as depth 1), and 16,384 UTF-8 bytes of authored JSON text.
Each operation rejects a compact serialized result larger than 16,384 UTF-8
bytes at the operation node. Duplicate object property names are rejected.
There is no implicit conversion between `json`, `array<json>`, or primitive
array types. These JSON array operations do not add object equality,
membership, or canonical object ordering. JSON-valued graph-function ports,
workflow-interface fields, MCP arguments, Sequence paths, loop-carried state,
parallel shared state, and checkpoint state remain separate contracts and
verification increments. The existing `JsonToArray` path continues to
produce `array<json>`.

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

## Follow-up: compose the existing JSON-array value

The existing `array<json>` identity is the collection of arbitrary JSON values
already produced by `JsonToArray`; it is distinct from one `json` value whose
root happens to be an array. Preserve its current heterogeneous-element,
ordered, JSON-null behavior. Do not add another type identity or normalize old
graphs into one.

The first composition increment applies to the current unified graph and
covers `ArrayLiteral`, `ArrayLength`, `ArrayGet`, `ArrayAppend`, `ArrayInsert`,
`ArrayReplace`, `ArrayRemove`, and `ArrayConcatenate`. JSON-valued items may be
null, scalar, object, or nested array. Exact pin types remain required. The
literal input is limited to 16,384 UTF-8 bytes before parsing, including
whitespace. The outer array is limited to 64 items; the parsed document's root
container counts as depth 1 toward the 32-level JSON depth limit. Every
operation result is limited to 16,384 UTF-8 bytes after compact serialization;
a transform that exceeds the result limit fails at that node before returning
or recording the value.

Array transforms copy `JsonElement` values through `Utf8JsonWriter.WriteTo`,
preserving object member order and number-token spelling. String escape
spelling may normalize while preserving the decoded string value. These bytes
are deterministic preservation of the supplied JSON representation, not a
canonical JSON form. Structural equality, membership, canonical object-key
ordering, and value hashing are outside this increment. A `json` value and an
`array<json>` value never connect implicitly.

JSON-valued parameter support is implemented by [Decision
0068](0068-typed-array-workflow-parameters.md), including bounded
default/override parsing, compact run text, typed getter pins, child mappings,
and playback through the existing string-valued parameter maps. JSON-valued
function support remains separate and must account the exact retained UTF-8
representation against the existing 65,536-byte per-invocation evidence
budget. JSON-valued items may be added to the private iterator candidate proof
after composition works, but this does not close the integrated lifecycle,
budget, playback, or nearest-frame `Break` gate in [Decision 0070](0070-bounded-array-iteration-contract.md).

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
