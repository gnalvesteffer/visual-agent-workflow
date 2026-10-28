# Canonical explicit pin resolution

**Status: Accepted with bounded scope**  
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-27**

## Context

Explicit graph pins are a portable contract shared by persistence, validation,
the editor, and execution. Direct MCP nodes already persist schema-derived input
pins, but Studio also reconstructed a second descriptor list when a live binding
was available. That made it possible for a pin to be displayed with different
metadata from the contract used by validation.

## Decision

`WorkflowPorts.Explicit` is the canonical resolver for current explicit pin
metadata. It accepts optional, authoring-time MCP discovery metadata only when a
node has no persisted MCP pin list. It derives the full descriptor, including
stable identity, display name, type, direction, kind, requiredness, cardinality,
and static-value support.

Studio may parse a live MCP schema to discover an insertion/configuration shape,
but it passes that normalized result to the Core resolver. The resolver does not
persist, mutate, or silently replace a node's stored pin contract. A persisted
MCP pin list always wins over live discovery.

Invalid data edges now report the source and target pin IDs and declared types
when those pins are known. The editor uses the same exact-identity rule when
rejecting a connection, and directs authors toward an explicit conversion node
instead of implying an automatic coercion.

## Compatibility and boundaries

- No schema version changes are required.
- Existing definitions, canonical hashes, runtime argument precedence, and
  schema 1.36/1.37 behavior remain unchanged.
- Nullability, typed defaults, implicit conversions, dynamic-pin migration, and
  omission-versus-explicit-null semantics remain deferred. They need their own
  value, runtime, compatibility, and evidence contracts.
- Live binding changes cannot erase persisted pins or invent defaults.

## Proof

Core tests verify that discovered MCP metadata produces complete descriptors and
cannot override persisted metadata. Validator tests also verify actionable
source/target type diagnostics. Existing validator and runtime tests retain
coverage for required pins, static arguments, duplicate edges, round trips, and
connected-value precedence.
