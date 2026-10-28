# Static descriptor validation boundary

Status: accepted (2026-10-28)

## Decision

Core validates the internal conformance of the currently registered static node
descriptors at the workflow-definition boundary. A static descriptor must have
an exact current descriptor version, a configuration kind, unique port IDs,
supported data types, valid direction/kind combinations, valid cardinality and
static-value metadata, and flags that agree with its node-family contract.

The validator reports these failures as `node.descriptor` issues. Existing
`node.unsupported` and schema-specific diagnostics remain the owner of unknown
node types, node implementation versions, and schema admission. Drafts remain
saveable; publish and execution continue to use the existing validation gate.
Node identity and implementation-version checks now read from the canonical
node contract catalog rather than duplicating a version literal in the
validator.

## Why this boundary is narrow

The descriptor resolver is intentionally schema-neutral and is also used for
authoring projections. Dynamic Agent, MCP, Sequence, parallel, and graph-
function pins depend on persisted configuration and, for MCP, optional live
discovery. They are not admitted by this conformance check. Dynamic descriptor
validation remains deferred until descriptor identity, schema applicability,
nullability/default rules, migration, and malformed-configuration behavior are
defined together.

This avoids treating an authoring projection as a replacement for the
schema-specific validator and avoids requiring live capability discovery to
validate portable definitions.

## Compatibility and evidence

The check does not add fields, change schema versions, or persist descriptor
snapshots. It only verifies code-owned static metadata and keeps the existing
invalid-draft lifecycle. Core tests cover valid registry descriptors, malformed
metadata invariants, exact version handling, and validation of a definition
containing static descriptor nodes. Dynamic resolution tests continue to prove
that persisted MCP pins take precedence over transient discovery, but that
projection is not an admission contract.
