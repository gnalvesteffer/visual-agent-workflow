# Decision 0060: Unified authoring schema

**Status:** Accepted and in progress (2026-10-29)

## Decision

Newly authored workflows use schema `2.0`, a unified authoring contract. The
editor must expose the implemented node families and graph features from one
current contract; authors do not perform a chain of schema promotions to reach
features already shipped by the platform.

Historical 1.x definitions retain their serialized bytes, hashes, revisions,
and runtime semantics. Loading a legacy definition does not silently rewrite
it. A later increment will provide an explicit, reviewable normalization to
2.0 with source/target provenance and deterministic diagnostics.

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

The first increment registers 2.0, makes new explicit templates use it, and
routes ordinary unified graphs through the explicit validator. Follow-up
increments must complete unified validation, runtime plan selection, editor
library behavior, normalization, package/reuse support, and regression proof.

