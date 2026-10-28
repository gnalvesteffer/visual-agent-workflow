# Sequence Agent topology analyzer

**Status:** Implemented proof seam; public schema promotion deferred  
**Date:** 2026-10-28

## Decision

Add a pure Core analyzer for the proposed next Sequence path contract without
adding schema 1.44 or changing validator admission, Studio authoring, package
compatibility, runtime dispatch, or checkpoint support.

The analyzer examines the persisted execution graph and describes each ordered
Sequence output path. It accepts either an existing direct-MCP-only path
(including a direct path to `Output`) or exactly one plain-text Agent
immediately before `Output`.

Agent paths must be linear and disjoint. The Agent must have a provider and
instructions and must not declare tools, skills, delegates, graph-function
tools, routes, structured results, approvals, loops, branches, parallel
configuration, or workflow calls. The analyzer also rejects missing endpoints,
cycles, shared descendants, extra successors, invalid execution pins,
unreachable nodes, and paths that do not arrive at `Output`.

## Boundary

The result is a deterministic topology description and stable diagnostic list,
not a runtime plan or an authorization decision. The existing schema 1.43
validator continues to reject Agent descendants, and the schema catalog remains
unchanged. This prevents a non-public proof from accidentally becoming a
serialized compatibility promise.

## Promotion gates

Schema 1.44 remains blocked until runtime execution derives its plan from the
persisted graph, Agent restrictions are enforced at runtime, failure/timeout/
cancellation and sibling settlement are evidenced deterministically, playback
preserves Sequence/Agent lineage, and package, checkpoint, Studio, provider,
and host-authority boundaries are covered by persisted-graph tests.

## Verification

Core regression tests cover a valid terminal Agent candidate, mixed MCP/Agent
rejection, and rejection of Agent capabilities that widen the bounded shape.
The valid candidate is deliberately still rejected by the schema 1.43 validator;
that assertion proves the analyzer is non-authoritative until the promotion
gates are met.
