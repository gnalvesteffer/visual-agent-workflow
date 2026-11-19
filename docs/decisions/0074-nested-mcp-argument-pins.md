# Decision 0074: Bounded proof for nested MCP argument pins

**Status:** REVISE; proof-only work may proceed, authoring and runtime admission remain unchanged

**Date:** 2026-11-19

## Context

Unified workflow graphs expose each supported top-level MCP argument as a
typed, connectable input pin. Nested objects and arrays can be passed through a
`json` pin, and the normalized MCP schema descriptor retains their bounded
structure, but nested fields are not separate graph pins. The persisted
top-level mapping and connected-over-static behavior are covered by
[Decision 0034](0034-mcp-argument-descriptor-convergence.md); the descriptor
boundary is documented in [Decision 0020](0020-mcp-schema-descriptor.md).

Separate pins for tool inputs match the Blueprint-inspired exposed-input goal
and the existing authoring direction. Flattening every nested field directly
into the current pin model, however, would create unresolved questions about
stable path identity, parent/child overlap, optional omission, arrays, schema
drift, migration, and whether values leak through data-evaluation evidence.
The current broker still validates the complete invocation against its pinned
contract, live schema, and host grant; nested pins must not replace that
authority.

## Adjudicated direction

The three-role review returned **REVISE**. Proceed with a bounded, non-public
proof for explicit nested object field pins. Do not add Studio authoring,
public/package admission, or new persisted invocation-value evidence until the
proof establishes an argument-redaction contract and compatibility behavior.
Existing top-level pins and JSON-valued object/array inputs keep their current
meaning and remain available throughout the proof.

The eventual representation should identify schema fields by stable property
paths rather than display labels, property ordering, or discovery-time numeric
positions. The proof must use the existing shared descriptor-to-pin mapping
seam; editor, validator, and runner must not grow independent path mappings.
Pins for overlapping parent and descendant paths must be rejected rather than
resolved by incidental ordering. Defaults remain annotations and are not
silently inserted. Broker schema and grant checks remain mandatory at dispatch.

## Required proof gates

Before enabling nested pins in authoring or runtime admission:

1. Prove one bounded closed-object shape with deterministic property-path and
   pin identity, one required scalar child, one optional child, and a static
   leaf combined with a connected leaf.
2. Reject parent/descendant overlap, duplicate paths, missing required nested
   values, invalid types, and values outside schema bounds. Do not infer array
   length from connected item pins; arrays remain on the existing JSON pin
   path until a separate explicit item-shape contract exists.
3. Reconstruct the exact nested MCP argument object from connected and static
   values, then verify the real broker independently rejects changed pinned
   contracts, invalid live schemas, and missing node grants.
4. Define and test redaction before nested values can be persisted in
   `data.evaluated` or exposed in run details, playback, diagnostics, or error
   messages. The definition must cover connected and static values, nested
   paths, and bounded correlation metadata without recording raw sensitive
   values.
5. Define explicit migration and schema-drift behavior. Existing top-level
   pin IDs, definitions, hashes, package behavior, and old-reader interpretation
   must remain stable; no discovery refresh may silently rewrite authored pins
   or connections.

If these gates require a different value/evidence contract, return for another
architecture review before broadening the proof. A successful proof does not
by itself establish general nested pins, array-item pins, unrestricted JSON
Schema support, or Blueprint parity.

## Evidence and follow-up

The current implementation already supports nested MCP values on JSON pins and
retains normalized nested schema metadata. It does not yet provide the
path-level pin contract or the redaction and migration guarantees above. Track
the implementation and verification gates in the [Blueprint parity matrix](../blueprint-capability-matrix.md)
and [roadmap](../roadmap.md#planned-phase-blueprint-inspired-authoring-contracts-and-ergonomics).
