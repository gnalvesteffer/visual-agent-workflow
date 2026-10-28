# Static pure pin descriptors

**Status: Accepted and implemented as a bounded slice**  
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-28**

## Decision

The first descriptor-registry increment covers only static pure-value nodes:

- string, Boolean, integer, and finite-number constants;
- text concatenation and string equality;
- checked integer addition and integer equality;
- Boolean NOT.

`WorkflowNodeDescriptorRegistry` owns exact, versioned pin descriptors for
this set. `WorkflowPorts` resolves the same descriptors for explicit and legacy
data-port queries, so typed connection checks and editor-facing pin metadata
share one source of truth. The descriptors are declarative metadata only; they
do not grant host capabilities, perform discovery, or replace schema-specific
validation.

The descriptor contract version is `1.0.0` and is deliberately separate from
`NodeDefinition.TypeVersion`. Display/category metadata remains outside the
portable definition and therefore does not affect definition or package hashes.
An unknown descriptor version fails closed; there is no fallback to the newest
descriptor. Existing schema gates remain authoritative for whether a node is
legal in a particular workflow schema.

Dynamic/configuration-derived nodes remain on specialized paths for now:
MCP arguments, Agent routes, parallel/collaboration pins, graph-function ports,
and workflow-value typing. Nullable values, defaults, conversions, migrations,
and runtime enforcement directly from descriptors are also deferred.

## Proof

Core tests verify registry uniqueness and version identity, exact descriptor
parity with `WorkflowPorts`, unknown-version rejection, typed pin identities,
and legacy serialization/hash behavior. Existing MCP discovery tests continue
to prove persisted pin metadata takes precedence over live discovery.

This is a bounded descriptor increment, not completion of the broader Blueprint
descriptor phase. The roadmap continues to track dynamic descriptors, schema
migration policy, and full validator/runtime/editor convergence.
