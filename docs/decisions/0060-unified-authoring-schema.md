# Decision 0060: Unified authoring schema

**Status:** Accepted and in progress (2026-10-29)

## Decision

Newly authored workflows use schema `2.0`, a unified authoring contract. The
editor must expose the implemented node families and graph features from one
current contract; authors do not perform a chain of schema promotions to reach
features already shipped by the platform.

Historical 1.x definitions retain their serialized bytes, hashes, revisions,
and runtime semantics. Loading a legacy definition does not rewrite stored
source or published revisions. `WorkflowSchemaNormalization.NormalizeForEditing`
creates a separate current draft, preserving graph identities and embedded
snapshots. It accepts explicit graphs, verified implicit linear text graphs,
including unrouted Agent nodes, and a bounded legacy conditional shape. For
implicit linear graphs, original data edges are retained and explicit execution
edges connect Input, Agents, and Output; pure text transforms remain lazy data
nodes. Unsupported control-flow
translations return diagnostics. Saving remains an authored edit.

Legacy conditional schemas 1.4–1.22 normalize when there is exactly one
Condition, at most one Merge, one linear path of pure uppercase/reverse nodes
before the Condition, and disjoint linear pure-text arms. If the arms already
reconverge at one Merge, the conversion also requires one linear pure-text path
from that Merge to Output. If both arms instead terminate directly at Output,
normalization inserts one deterministic Merge, retargets the two terminal data
edges to it, and connects its output to Output. It adds explicit activation
edges for Input, the two selected Condition outputs, Merge, and Output. This
preserves legacy output behavior while using the current runtime's active-route
data selection; direct Condition-to-Output execution edges alone would not
select between multiple data inputs. Original revisions remain unchanged, and
the source and synthesized target are recorded in normal conversion
provenance. Extra control nodes, shared arm nodes, bypasses, and ambiguous
paths remain unchanged with diagnostics. The current Merge data pin accepts
1–16 alternative incoming values; runtime selects the value belonging to the
active execution route without evaluating the unchosen arm.

The schema-1.45 explicitly wired bounded loop-variable graph is additionally
qualified through the unified schema: normalization preserves node, data, and
execution-connection identities, leaves the legacy definition unchanged, and
produces the same output and recorded loop events when executed. This proves
that already explicit loop topology can be carried into schema 2.0; it does not
qualify other historical control-flow families or establish persisted
normalization provenance.

Normalization now returns a deterministic result with a stable normalizer
version, source and initial-target schema/hash, optional exact source revision,
and structured diagnostics. The provenance retains both bounded definition
snapshots, so the source remains recoverable even when no published source
revision exists. Successful conversions are saved atomically with
the current draft and layout; unsupported conversions return diagnostics and
leave the legacy draft unchanged. SQLite schema 6 stores the immutable
conversion record and an integrity hash. Later draft edits preserve the record
as evidence of the initial conversion, while restoring another revision clears
it. This evidence is separate from executable migration provenance and does
not rewrite historical source or publication history.

Legacy schema-1.17 through 1.22 data-carried loops with a linear body of pure
uppercase/reverse transforms now normalize to schema 2.0. Normalization retains
node and data-connection identities, adds four explicit activation wires, and
leaves the source untouched. In this topology, the Loop input has one initial
data edge and one body-return edge; execution iteration selects which edge is
evaluated, and LoopBody/Loop outputs expose the current runtime value. Tests
compare legacy and normalized output, iteration evidence, predicate-false and
maximum-iteration exits, and repeated per-pass transform evaluation. This does
not qualify effectful legacy loop bodies, nested/control-flow loops, or arbitrary
data cycles for migration. Schema-1.45 variable loops retain their separate
explicit VariableGet/Set topology.

Schema 2.0 does not remove host-owned security or correctness boundaries:
typed pin compatibility, graph boundedness, MCP grants, approvals, provider
contracts, credentials, token/time limits, and failure/cancellation semantics
remain independently enforced.

## Rationale

The 1.x promotion chain accurately records historical delivery, but it has
become the wrong authoring model: version identity is currently reused as a
capability switch in validation, runtime dispatch, and the Studio palette.
Consolidating authoring requires deriving the execution plan from node
contracts, configuration, and topology while keeping 1.x compatibility paths
for playback and existing projects.

## Implementation boundary

Schema 2.0 uses common node-configuration checks and typed graph validation.
Sequence, loop, and parallel regions select their existing bounded contracts
from graph contents. Internal compatibility views do not change revision
identity or create author-facing capability switches.

The editor has one node library and node property forms. Feature promotion,
schema preview, and enable-capability buttons are removed. Existing public
1.x editing/migration APIs remain available to consumers of historical assets.
They are compatibility APIs rather than the current Studio authoring path.

New current graphs combine parameters, scalar operations and conversions,
pure functions, JSON composition, Agents, typed MCP calls, approvals, explicit
branches and termination. Bounded Sequence paths, variable loops, static
parallel regions and collaboration retain their implemented topology limits.
This consolidation makes existing capabilities available together; it does
not introduce nested parallelism, unrestricted cyclic execution, implicit
type coercion, or new authority grants.

