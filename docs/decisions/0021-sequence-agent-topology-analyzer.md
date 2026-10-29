# Sequence Agent topology analyzer

**Status:** Implemented analyzer seam; schema 1.46 mixed-path promotion is qualified separately
**Date:** 2026-10-28

## Decision

Add a pure Core analyzer for the proposed next Sequence path contract without
adding schema 1.44 or changing validator admission, Studio authoring, package
compatibility, runtime dispatch, or checkpoint support.

The analyzer examines the persisted execution graph and describes each ordered
Sequence output path. It accepts either an existing direct-MCP-only path
(including a direct path to `Output`) or zero or more direct MCP nodes followed
by exactly one plain-text Agent immediately before `Output`.

Agent paths must be linear and disjoint. The Agent must have a provider and
instructions and must not declare tools, skills, delegates, graph-function
tools, routes, structured results, approvals, loops, branches, parallel
configuration, or workflow calls. The analyzer also rejects missing endpoints,
cycles, shared descendants, extra successors, invalid execution pins,
unreachable nodes, and paths that do not arrive at `Output`.

## Boundary

The result is a deterministic topology description and stable diagnostic list,
not a runtime plan or an authorization decision. Schema 1.43 remains direct-MCP
only; schema 1.46 authoritatively admits the bounded mixed path and the normal
runner executes it. The analyzer remains non-authoritative and must not be used
as an authorization decision.

## Promotion gates

Broader topology remains blocked until its own runtime, failure, playback,
package, checkpoint, Studio, provider, and host-authority contracts are
reviewed and proven. The current 1.46 mixed-path contract does not authorize
branches, joins, loops, Agent-owned tools, or arbitrary downstream nodes.

## Verification

Core regression tests cover a valid terminal Agent candidate, a direct-MCP to
Agent path, mixed-path package round trip, and rejection of Agent capabilities
that widen the bounded shape. Schema 1.43 remains covered by direct-MCP package
round trips, while schema 1.46 mixed-path admission is covered by the
authoritative validator and normal runner tests.
