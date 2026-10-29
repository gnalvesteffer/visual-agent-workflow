# Decision 0034: descriptor-driven direct-MCP argument convergence

## Status

Accepted and implemented as a bounded Blueprint-inspired descriptor slice.

## Context

Schema 1.37 already persisted one typed top-level pin for each discovered MCP
tool argument. The canvas and Core port projections used the resolved pin
descriptor, while validation and runtime argument construction separately read
`McpInputPorts`. That duplicated the pin-to-property mapping and could let
editor, validation, and dispatch semantics drift.

## Decision

Core now exposes one non-serialized `WorkflowMcpArgumentDescriptor` mapping that
pairs each resolved input pin with its persisted MCP property identity. The
direct-MCP validator and Agent Framework runner use that mapping for:

- stable pin IDs and graph data types;
- requiredness and at-most-one connection checks;
- connected values overriding authored static JSON values; and
- reconstruction of the JSON object sent to the broker.

Malformed, duplicate, incomplete, or type-inconsistent mappings fail closed.
MCP schema defaults remain annotations; they are not silently copied into
workflow arguments. The broker still performs live schema and authorization
checks at dispatch.

This does not serialize descriptors, migrate historical pins, add nested pins,
or change the schema version. The persisted `McpInputPorts` list remains the
workflow-owned compatibility identity; the descriptor is a derived contract.

## Proof

Core tests verify the pin/property identity mapping and fail-closed duplicate
identity behavior. Existing direct-MCP graph tests continue to cover required
pin satisfaction and invalid drafts. Agent Framework tests verify typed string,
integer, number, and connected/static argument behavior through the real runner.

## Follow-up

Expose connected, missing, and static-value status in the selected-node Inputs
inspector, then address nested schema descriptors and migration only through a
separate compatibility decision.
