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
snapshots. It accepts explicit graphs and verified implicit linear text graphs;
unsupported translations return diagnostics. Saving remains an authored edit.

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

