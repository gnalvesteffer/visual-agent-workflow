# Descriptor authority boundary

**Status: Adjudicated; descriptor remains non-authoritative**  
**Scope: Blueprint-inspired pin-shape convergence**  
**Decision date: 2026-10-28**

The Blueprint parity review examined whether `WorkflowNodeDescriptor` should become
the single source for authoring and workflow validation. The decision is to adopt
the descriptor as the source of **resolved pin shape** where a consumer already
uses it, but not as the authority for schema admission, node-family validation,
runtime dispatch, host capability, persistence, or historical playback.

The current resolver still derives configured descriptors from legacy
`WorkflowPorts` projections. Making it authoritative now would preserve the
dependency in the wrong direction and could make editor, validator, runtime,
checkpoint, or playback behavior disagree. It could also reinterpret immutable
historical definitions when a non-serialized descriptor changes.

## Bounded progress

- The graph canvas and editor insertion controls use the same resolved authoring
  descriptor for configured output pins, including Agent routes.
- Static descriptor registry conformance and advisory cross-layer drift tests
  remain in place.
- Schema-specific validators and runtime consumers retain their existing
  authority boundaries.
- Serialized workflow definitions, hashes, schema versions, grants, and recorded
  playback semantics are unchanged.

## Required gates before a future authority slice

- Derive static pin shape directly from canonical descriptors without a reverse
  dependency on legacy projections.
- Add schema-qualified resolution and distinguish resolved, incomplete,
  malformed, unsupported-version, and unavailable-discovery states.
- Keep live MCP discovery out of authoritative validation, admission, runtime,
  and playback.
- Audit and migrate Core, runtime, checkpoint, graph-editing, Studio, and viewer
  consumers together, retaining compatibility projections until equivalence is
  proven.
- Add historical playback, unchanged-hash descriptor evolution, malformed
  dynamic configuration, discovery drift, and editor/validator/runtime
  disagreement regressions.

Family-specific topology, capability, failure, and lifecycle rules remain
separate from generic descriptor shape even after these gates are met.
