# MCP schema descriptor

Status: implemented bounded slice

## Decision

Expose a versioned, read-only `McpToolSchemaDescriptor` derived from the
validated and normalized MCP input schema. It provides stable property paths,
JSON types, requiredness, nested object/array structure, enums, bounds,
bounded descriptions, and default values as annotations. Only top-level scalar
fields are marked eligible for the existing direct graph-pin model; nested
fields remain described JSON structure and are never flattened implicitly.

The descriptor is an MCP-layer authoring and diagnostics view. It is not stored
in workflow definitions, does not change content hashes, does not create graph
pins, does not apply defaults, and does not grant tool authority. The broker's
existing schema validation and contract-hash checks remain the only runtime
dispatch authority.

## Compatibility boundary

`McpInputPortDefinition` and all existing workflow schemas remain unchanged.
JSON Schema defaults remain annotations: an omitted optional property is not
inserted into an invocation. Unsupported keywords and bounded-schema failures
continue to fail closed through the existing validator. The descriptor's
normalized-schema hash is derived metadata for comparison and diagnostics, not
a replacement for the pinned MCP contract hash.

## Proof

MCP regression tests cover deterministic descriptor version/hash output, nested
object and array metadata, required and optional fields, enum and scalar
bounds, direct-pin eligibility, default annotations, omission semantics, and
the existing unsupported-schema and runtime validation paths. No credentials,
grants, or argument values are included in the descriptor.

## Deferred

Nested typed graph pins, schema-driven default adoption, automatic pin
regeneration or migration, arbitrary JSON Schema composition, and richer
redacted invocation evidence require separate reviewed contracts.
