# Decision 0077: Fork/Join inside a bounded Loop body

**Status:** Accepted and implemented in unified schema 2.0. The single bounded
Loop may contain a complete static Fork/Join region as one body step. Nested
Loops, parallel regions outside the Loop body, and collaboration rendezvous in
the body remain unsupported.

**Date:** 2026-11-23

## Context

Blueprint-inspired graph composition requires bounded iteration and parallel
work to compose in one workflow. Before this decision, `ValidateUnifiedLoopGraph`
required one linear processing path, and the ordinary runner discarded the
Loop iteration number when a Fork dispatched branches. The topology experiment
in `LoopFeedbackTopologyProofAnalyzer` was not authoritative and could not
admit the graph by itself.

This extends the current unified graph contract without introducing a new
schema, capability flag, or author-facing depth limit. Existing Loop and
Fork/Join graphs keep their serialization.

## Decision

The Loop validator treats a complete static Fork/Join region as one step in
the Loop body. It follows the matching Join's sole continuation to find the
body return, and admits the return only when it reaches the Loop feedback input
after the Join. Parallel topology, stable branch IDs, matching joins, branch
ownership, nested region ownership, and path validity continue to be checked by
the existing parallel validator. A workflow containing a Loop cannot place
Fork, Branch, or Join nodes outside the Loop body.

The runtime preserves `LoopIteration` on each Fork branch message and on both
Join completion paths. Each static Fork activation therefore retains its own
activation identity and the Loop pass that admitted it. Branch failure or
cancellation prevents Join completion; without a successful Join there is no
Loop feedback delivery or completed iteration.

The body can also contain linear processing steps before and after the region,
and the parallel validator's existing finite graph bounds continue to bound
region size and nesting. The workflow still supports one predicate Loop only.

## Proof and limits

Focused Core unit tests verify a valid loop-owned region and reject direct
branch-to-feedback escape and parallel nodes outside the Loop body. Public
Agent Framework runner unit tests verify two repeated passes, distinct Fork
activation IDs, exact branch results, and suppression of Join completion and
Loop iteration settlement on branch failure or cancellation. These are
in-process deterministic unit tests; no browser test, external model, or MCP
server is involved.

This does not add nested Loop support, collaboration rendezvous inside a Loop,
or Loop nodes inside parallel branches. Workflow-variable updates inside
parallel branches remain governed by the existing parallel branch node subset.
Passive playback already groups Loop and Join evidence by scope and recorded
position; the new unit tests establish runtime scopes and join outcomes but do
not add a separate persistent-store reopen proof.

See [bounded loop graphs](../loop-graphs.md), [parallel graphs](../parallel-graphs.md),
the [Blueprint capability matrix](../blueprint-capability-matrix.md), and the
[roadmap](../roadmap.md).
