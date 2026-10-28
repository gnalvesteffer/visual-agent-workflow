# Pure graph-template proof seam

Status: accepted bounded proof; Studio exposure deferred

## Decision

Add a Core-only contract for authoring templates built from the existing pure graph-function subset. A template is an immutable, typed data-graph snapshot that is expanded during authoring into ordinary graph nodes. It is not a runtime call, a callable workflow revision, or a capability-bearing dependency.

The first proof provides:

- validation of template identity, version, description, graph-function body, effect flags, and dynamic-pin restrictions;
- extraction from an already validated pure graph-function snapshot;
- copy-on-expand with caller-supplied or generated fresh node identities;
- connection remapping and an informational source-node map;
- existing graph-function limits, acyclic evaluation, typed boundary, and unsupported-node checks.

The proof also now extracts a selected workflow data subgraph when callers provide
explicit typed input/output boundaries. Extraction rejects hidden incoming data,
execution wires, effectful nodes, dynamic pins, unsupported nodes, and boundary
type mismatches; it never infers a boundary from a visual selection.

Template documents now have a strict `1.0` envelope containing the definition and
its lowercase SHA-256 content hash. The hash covers only the canonical definition,
not the envelope. Imports reject duplicate or unknown properties, wrong format
versions, malformed/null shapes, payloads over 1 MiB, and invalid template
content. Expansion rechecks generated IDs and the existing pure graph node and
connection bounds; target-workflow combined limits remain the responsibility of a
future insertion operation.

The source map is non-live provenance. Expansion does not synchronize with the source and does not transfer credentials, grants, model connections, MCP bindings, host capabilities, or runtime subscriptions.

## Boundaries

Templates remain distinct from:

1. graph functions, which are pinned pure computations evaluated at runtime;
2. reusable workflows, which execute pinned workflow revisions with orchestration semantics;
3. future macro assets, which may add project persistence and editor extraction only after this proof establishes the contract.

No Studio picker, project asset, workflow schema promotion, runtime template node, or automatic upgrade path is included in this increment.

## Follow-up gates

Before editor exposure, add dependency closure, layout and undo semantics,
project/catalog persistence, and a user-facing distinction between templates,
graph functions, and workflow calls. Revisit provenance and upgrade diagnostics
before introducing a published project asset.

## Review

An independent proponent supported the bounded slice, an opponent identified abstraction overlap and boundary risks, and an adjudicator approved only the Core proof seam while rejecting immediate Studio exposure. This decision follows the adjudicator's narrower recommendation.
