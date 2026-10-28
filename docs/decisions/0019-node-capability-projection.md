# Node capability projection

Status: implemented bounded slice

## Decision

Add a versioned, in-memory `WorkflowNodeCapabilityProjection` that composes the
canonical node contract catalog with the existing descriptor resolver. The view
exposes the node family/version, configuration kind, resolved pins, declared
limits, host-capability indicator, and whether the shape is static, configured,
or transiently enriched by MCP discovery.

The projection is not a new workflow field. It is not serialized, hashed,
published, executed, or treated as a host grant. MCP discovery may annotate the
view for authoring, but persisted MCP pins remain authoritative and discovery
does not introduce credentials, endpoints, grants, or runtime authority.

## Boundary

Schema-specific validators remain authoritative for admission and draft
behavior. Runtime adapters remain authoritative for execution and capability
checks. Unknown projection versions fail closed. Static descriptors and
configuration-derived descriptors continue to use their existing exact-version
and dynamic-resolution rules. No live MCP connection is required to resolve a
node with persisted pins.

## Review and proof

The required adversarial review used independent proponent and opponent reports
followed by a separate adjudicator. The adjudicated recommendation was to
proceed with this revised, Core-only Candidate B rather than add serialized
descriptor fields or make the projection drive runtime/schema behavior.

Focused Core tests cover static Boolean Branch metadata, configured Agent and
Sequence pins, persisted-versus-discovered MCP pins, unchanged definition
hashes, host-capability non-authority, and fail-closed projection versions.

## Deferred

This does not complete Blueprint parity. Descriptor-driven schema admission,
runtime enforcement, migration, richer type/default/nullability semantics, and
full Studio convergence remain roadmap work.
