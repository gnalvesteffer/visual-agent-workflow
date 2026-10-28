# Schema capability catalog

Status: accepted (2026-10-28)

## Decision

Core maintains one immutable catalog for the exact serialized workflow schema
identities currently supported by the validator: `1.0` through `1.43`. Each
entry records the literal version, support status, explicit-graph capability,
and direct-MCP-pin capability. Identity is ordinal and exact; numeric ordering
does not imply feature support.

`WorkflowSchemaVersions` keeps its existing constants and method signatures as
compatibility wrappers. The wrappers preserve the deliberately non-monotonic
feature sets and the definition-specific schema-1.29 rule where an explicit
execution-connection collection changes graph classification. The validator's
top-level supported-version check uses the catalog, while its other exact
feature gates remain unchanged until they can be modeled without losing their
individual compatibility rules.

## Boundaries

This catalog is not a migration registry. It does not transform definitions,
change JSON, alter canonical hashes, grant support to future versions, or
replace schema-specific validation. Existing migration and promotion methods
remain explicit and produce new definitions/drafts according to their existing
contracts.

## Evidence

Core tests independently enumerate every supported version, reject null,
whitespace, case-variant, and near-miss identities, assert the exact
non-monotonic feature sets, preserve the schema-1.29 connection condition, and
prove that catalog metadata does not enter workflow JSON or hashes.
