# Dynamic descriptor conformance

Status: accepted (2026-10-28)

## Decision

Resolved descriptors for configuration-derived node families now pass through
the same schema-neutral pin metadata checks as static descriptors. The Core
validator applies this boundary to Agent, MCP, Sequence, parallel,
workflow-call, and graph-function projections whenever no static descriptor is
registered.

The dynamic check validates supported data types, direction/kind combinations,
cardinality, requiredness, and pin identity within a direction/kind namespace.
It intentionally permits an incomplete dynamic configuration to resolve to no
pins, and it does not assert the final pin count or topology: those remain
owned by schema-specific validation. Execution outputs may be required, and a
dynamic node may reuse a textual pin ID between a data pin and an execution pin;
both are established graph semantics.

Live MCP discovery remains optional authoring input. Discovery is not required
for imported-definition validation, and the validator never persists or
executes transient descriptions.

## Evidence and boundary

Core tests cover resolved Agent, MCP, and Sequence descriptors, optional live
MCP metadata, existing malformed static descriptors, and the full validator
suite. The static registry still owns strict static descriptor conformance;
dynamic configuration-specific identity, schema admission, host capability
checks, and runtime enforcement remain separate contracts.
