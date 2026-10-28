# Pure graph-template proof seam

Status: accepted bounded proof; catalog-backed Studio insertion authoring added; source/extraction and upgrade work deferred

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

Catalog-backed Studio projects now have a separate `graph-templates.json` authoring
catalog with an outer catalog format, exact ID/version lookup, immutable additions,
logical folder moves, bounded aggregate size, strict load validation, and atomic
same-directory replacement. Folder-backed projects and the project source archive
do not consume this catalog yet; that is an explicit future ownership decision.

The Core authoring API also supports transactional insertion of a validated
template into a validated workflow. Callers bind every declared template input to
an existing data output and every declared template output to an existing data
input. Boundary nodes are omitted, boundary edges are translated, existing
single incoming data edges are replaced, and all copied node and connection IDs
are fresh and injectable for deterministic tests. The result includes the new
immutable definition, source maps, and complete removed-edge preimages so an
editor can record one undo entry without reconstructing an inverse. Target
validation, exact type/cardinality checks, pure-node restrictions, combined
limits, and final whole-workflow validation happen before the result is returned.

The source map is non-live provenance. Expansion does not synchronize with the source and does not transfer credentials, grants, model connections, MCP bindings, host capabilities, or runtime subscriptions.

## Boundaries

Templates remain distinct from:

1. graph functions, which are pinned pure computations evaluated at runtime;
2. reusable workflows, which execute pinned workflow revisions with orchestration semantics;
3. future macro assets, which may add project persistence and editor extraction only after this proof establishes the contract.

The catalog-backed Studio editor now exposes exact template revisions through a dedicated insertion dialog. The dialog requires one compatible typed source per template input and one compatible typed destination per template output, previews copied nodes/connections and incoming-edge replacements, and applies the Core transaction as one editor change. Folder-backed/source-archive projects intentionally do not expose this catalog. No workflow schema promotion, runtime template node, or automatic upgrade path is included in this increment.

## Follow-up gates

Before broader asset exposure, add dependency closure, template layout assets, folder-backed/source-archive persistence, and upgrade diagnostics. Keep the existing dialog's one-change undo behavior and explicit typed bindings covered while adding a user-facing distinction between templates, graph functions, and workflow calls. Revisit provenance before introducing a published cross-project template asset.

## Review

An independent proponent supported the bounded slice, an opponent identified abstraction overlap and boundary risks, and an adjudicator approved only the Core proof seam while rejecting immediate Studio exposure. This decision follows the adjudicator's narrower recommendation.
