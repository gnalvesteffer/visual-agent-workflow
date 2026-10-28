# Dynamic pin descriptor resolution

Status: Implemented as a schema-neutral Core projection (2026-10-28)

## Decision

Visual Agent Workflow exposes one immutable Core resolver for the effective pin
shape of a configured node. Static pure-node descriptors continue to come from
`WorkflowNodeDescriptorRegistry`; configuration-derived families are projected
from the existing `NodeDefinition` fields. `WorkflowPorts.Explicit` delegates
to that resolver, while its legacy `Inputs` and `Outputs` helpers retain their
existing behavior.

The projection preserves authored IDs and order for Agent routes, Sequence
outputs, parallel branches, and graph-function interfaces. MCP pins use
persisted `McpInputPorts` when present. Optional discovered MCP metadata may
fill only an absent persisted contract and descriptions are copied into the
transient descriptor result; discovery never changes the node, definition hash,
portable JSON, validation admission, runtime arguments, grants, or authority.

The resolver is shape metadata, not schema validation. It does not add schema
versions, migrate dynamic pins, infer defaults or conversions, or authorize a
capability. Existing validator branches remain responsible for deciding which
configuration is legal in each schema, and the runtime remains responsible for
execution and static-argument precedence.

## Evidence and limits

Core tests cover static parity, MCP persisted-versus-discovered precedence,
explicit empty MCP metadata, transient descriptions, Agent and Sequence
labels/order, parallel fork/join pins, graph-function ports, immutability, and
unknown-node fail-closed behavior. Existing schema, validator, package, and
runtime tests remain the compatibility boundary.

This does not ship richer nullable/default/container types, dynamic-pin
migrations, descriptor-driven runtime validation, or new Studio controls. Those
remain separate Blueprint parity slices.
