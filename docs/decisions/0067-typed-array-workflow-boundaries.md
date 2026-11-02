# Decision 0067: Typed array workflow boundaries

**Status:** Implemented in schema 2.0  
**Date:** 2026-11-02  
**Scope:** Bounded `array<T>` values at workflow Input and Output nodes

## Context

Decision 0066 defines immutable, bounded primitive arrays in pure dataflow.
Leaving the values trapped inside the graph prevents callers from supplying
collections and workflows from returning them. The existing public execution
API accepts a string input and returns string output, so changing those APIs
would add a separate compatibility and hosting concern.

The required adversarial review agreed on a staged expansion with independent
proofs for each graph placement. The review left the order of boundary,
parameter, and function work open. This increment selects Input and Output as
the first boundary because both can preserve the existing runner API while
using the already established JSON representation and array bounds.

## Decision

In current schema 2.0, Input nodes may declare `array<string>`,
`array<boolean>`, `array<integer>`, or `array<number>`. Output nodes may declare
the same types. These are exact graph types: the data edge must match the full
element type. `array<json>` is not a primitive array and remains governed by
its existing JSON-array operators and contracts.

At the workflow boundary, callers provide one JSON array as the existing
string `input`, for example ` ["alpha", "beta"] ` (without the surrounding
spaces if desired). Studio keeps a single JSON text field and shows a type-
specific example and guidance; it never rewrites the caller's value. Input
validation uses the shared array parser and rejects non-arrays, null elements,
wrong primitive types, non-finite numbers, more than 64 elements, or encoded
JSON over 16 KiB. Empty arrays are valid because the node declares the element
type. Output serializes the typed array as compact JSON text through the
existing string return contract.

The graph definition stores the boundary type in the node's existing
`InputDataType` or `OutputDataType` field. Package round trips preserve those
fields, and the definition hash changes when the declared boundary type
changes. Runtime data-evaluation evidence records the typed parsed input and
typed output evaluation; playback consumes those events without reparsing or
reevaluating the graph.

This increment does not add arrays to workflow parameters, graph functions,
workflow interfaces, variables, direct MCP arguments, Sequence paths, loop or
parallel state, or checkpoints. Those placements remain separate roadmap
items with independent validation, package/hash, runtime, evidence, and
recovery proofs. No historical schema behavior changes and no schema
promotion is required.

## Verification

- Core validates supported current-schema input/output declarations and exact
  typed edges, while rejecting `array<json>` as a primitive boundary type.
- Package round-trip preserves boundary declarations and definition identity;
  changing a declared element type changes the hash.
- Runtime exercises all primitive element types, empty arrays, invalid JSON,
  mismatched types, nulls, non-finite numbers, and size limits, and verifies
  the returned JSON text and typed evaluation evidence.
- Studio exposes boundary type selection and JSON text input guidance.
- Decision 0066, architecture, graph-value guide, user guide, API guide,
  capability matrix, and roadmap distinguish this shipped slice from deferred
  placements.
