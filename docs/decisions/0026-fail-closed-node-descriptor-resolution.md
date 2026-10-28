# Decision 0026: fail-closed node descriptor resolution

## Status

Accepted as a bounded compatibility increment. Historical descriptor migration remains deferred.

## Context

Persisted nodes carry an implementation `TypeVersion`, while the non-serialized static pin registry carries a descriptor version. Static consumers previously selected the current descriptor by node type alone. That could let a node with an unsupported implementation version receive current pins, and dynamic/type-switch fallbacks could infer pins from the node type anyway.

## Decision

Core now exposes node-aware static resolution through `WorkflowNodeDescriptorRegistry.TryGetForNode`. It succeeds only when the node type, its persisted implementation version, and the current registered descriptor agree with the canonical node contract. `WorkflowNodeDescriptorResolver`, `WorkflowPorts`, validation, and capability projection use this exact gate.

Unsupported node versions therefore fail closed: they do not receive the latest static descriptor, dynamic projections, or type-only fallback pins. They remain inspectable through the ordinary unsupported-node diagnostics and cannot be admitted for execution.

Descriptor versions remain non-serialized and distinct from workflow schema, node implementation version, package identity, and revision hash. No historical descriptor entries or migrations are introduced by this increment.

## Compatibility boundary

- Current definitions and hashes are unchanged.
- Unknown or historical node versions do not silently acquire current pin semantics.
- Dynamic Agent, MCP, Sequence, parallel, and graph-function descriptors remain configuration-derived and are not promoted into the static registry.
- Live discovery remains transient authoring metadata and cannot select a descriptor or change persisted identity.

Before supporting historical descriptor behavior, define explicit descriptor compatibility entries, migration ownership, package admission, and passive playback behavior in a separately reviewed contract.
