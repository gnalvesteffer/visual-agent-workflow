# Node effect and authoring metadata

**Status:** Implemented as descriptive Core metadata  
**Date:** 2026-10-28

## Decision

Extend the canonical `WorkflowNodeContractCatalog` with two additive,
init-only metadata properties:

- `WorkflowNodeEffectFlags` identifies possible boundaries touched by a node:
  run-scoped state, host interaction, an external capability, or a child
  workflow.
- `WorkflowNodeAuthoringFlags` identifies stable editor-neutral capabilities:
  configuration, dynamic pins, context insertion, and inline editing.

The existing positional `WorkflowNodeContract` constructor and deconstruction
shape remain unchanged. The catalog remains the only family-level metadata
authority; configuration-specific pins remain owned by the descriptor resolver.

## Semantics and non-guarantees

Effect flags are descriptive inventory metadata. They do not grant authority,
select a provider, authorize a tool, assert idempotence, promise rollback or
durability, make retries safe, or imply exactly-once external effects. A flag
describes a possible boundary of the node family; runtime validation, host
policy, scheduling, persistence, and evidence remain authoritative in their
existing layers.

Authoring flags are intentionally small and framework-independent. They do not
name Blazor components, CSS, schema admission rules, permissions, or workflow-
specific behavior. They are suitable for capability-aware discovery and
documentation, not a replacement for the editor's schema-gated operations.

The catalog conformance checks require complete metadata, valid flags, dynamic
pin consistency, representative capability/effect relationships, and lifecycle
relationships between activation, state ownership, failure disposition, and
recorded evidence. The
non-serialized capability projection and Core-aware library entries expose the
same metadata without creating a second family catalog. Pure
nodes cannot declare an effect boundary; Agent and MCP nodes identify external
capability interaction; approvals identify host interaction; and workflow calls
identify child-workflow execution.

## Compatibility

The metadata is not a `WorkflowDefinition` field, package field, schema version,
runtime plan, grant, or hash input. Existing serialized workflows and canonical
hashes remain unchanged. No schema promotion or runtime behavior depends on
these flags.

## Verification

Core tests cover catalog coverage and uniqueness, conformance, representative
pure/control/approval/Agent/MCP/child-workflow semantics, mutation detection for
lifecycle drift, and the guarantee that metadata is absent from workflow JSON
and canonical hashes.
