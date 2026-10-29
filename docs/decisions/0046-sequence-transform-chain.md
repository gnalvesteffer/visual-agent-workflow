# Decision 0046: Bounded deterministic transform chains

**Status:** Implemented as schema 1.49

## Context

Schemas 1.47 and 1.48 establish explicit execution/data contracts for one terminal deterministic text step on each bounded Sequence path. The next Blueprint-parity roadmap item is broader downstream topology, but arbitrary transforms, child workflows, Agents after transforms, and richer control flow would introduce materially different scheduling, authority, and playback contracts.

## Decision

Schema 1.49 adds a deliberately bounded linear suffix for each Sequence output:

```text
zero or more direct MCP nodes
  -> zero, one, or two deterministic text steps
  -> Output
```

The suffix may contain at most one `core.sequence.uppercase` and at most one `core.sequence.reverse`, in either order. Zero-transform paths remain valid so the schema is additive for direct-MCP paths. Every transform retains typed `string` data pins, explicit `execute`/`then` execution pins, ordinary node lifecycle evidence, and Sequence lineage. A transform may only be followed by the other transform or Output; direct MCP, Agent, WorkflowCall, routing, loops, and parallel nodes are not admitted after a transform.

Guarded editing promotes valid schema 1.47 or 1.48 definitions directly to 1.49. Promotion changes only the schema identity; stable node, port, edge, and layout identities remain unchanged. Older schemas remain readable with their previous terminal-only behavior, and older runtimes fail closed on 1.49 rather than silently broadening their contract.

## Deferred

This decision does not admit duplicate transform kinds, generic expressions, configurable transforms, child WorkflowCall nodes, Agent-after-transform paths, branches, joins, loops, approvals, or arbitrary downstream topology. Each requires its own activation, failure, authority, evidence, playback, and migration review.

## Verification

Core validates zero/one/two-step topology, transform ordering, duplicate rejection, and direct 1.47/1.48 promotion. The production runner executes both transform orderings through persisted schema 1.49 connections and records the normal Sequence/node lifecycle. The Studio exposes guarded promotion and schema-gated transform authoring. Full solution tests and documentation checks remain required before publication.
