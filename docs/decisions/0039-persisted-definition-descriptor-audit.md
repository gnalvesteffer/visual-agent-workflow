# Persisted-definition descriptor audit

**Status: Implemented bounded advisory slice**  
**Scope: Blueprint-inspired pin-shape convergence**  
**Decision date: 2026-10-29**

The Core now provides `WorkflowDefinitionDescriptorAudit`, a read-only audit of
persisted workflow node configuration. It resolves each node using only the
configuration stored in the definition, checks the resolved descriptor against
the existing Core port projection, and reports deterministic node paths and
diagnostic codes. Nested workflow-call definitions are audited recursively.

The audit deliberately does not consult live MCP discovery, grant authority,
change the definition, compute a new hash, replace schema validation, alter
runtime dispatch, or reinterpret historical playback. It is convergence
evidence while those authorities remain separate.

The audit fails closed for unknown node families, unsupported implementation
versions, unresolved descriptors, malformed resolved descriptor metadata, and
MCP argument mappings that do not map one-to-one to persisted typed ports. A
definition may still be an invalid draft; the audit reports descriptor drift
without turning advisory findings into a new admission rule.

The resolver exposes the same distinction as a typed result:
`Resolved`, `UnsupportedNodeType`, `UnsupportedNodeVersion`,
`MalformedConfiguration`, or `DiscoveryUnavailable`. Persisted resolution never
returns an empty descriptor for an unsupported node. The discovery state is
reserved for future transient authoring APIs and is not produced by the
persisted-definition resolver.

The schema-qualified `ResolveForDefinition` projection additionally rejects
legacy definitions and static nodes used before their minimum explicit schema.
This is a compatibility diagnostic only; it does not promote the descriptor to
the schema validator, runtime, checkpoint, package, or playback authority.

## Follow-up authority review

A second adversarial proponent/opponent/adjudicator review on 2026-10-29
considered promoting the current static registry to authoritative pin shape for
explicit graphs. The adjudicated outcome was **revise and proceed**: retain
the typed fail-closed resolution boundary, but do not promote descriptor
authority yet. The remaining blockers are schema-qualified descriptor identity,
historical hash compatibility, runtime and checkpoint plan binding, and passive
playback that cannot reinterpret old definitions through a changed code-owned
registry. Dynamic MCP, Agent, Sequence, parallel, graph-function, and variable
descriptors remain outside any authority promotion.

## Remaining gates

- Make static descriptors schema-qualified authorities only after validator,
  graph editing, runtime, package, checkpoint, and playback consumers migrate
  together.
- Define persisted dynamic descriptor identity and historical compatibility.
- Add explicit migration and source-preservation rules before changing any
  serialized descriptor contract.
