# Decision 0045: Bounded deterministic reverse paths

**Status:** Implemented as schema 1.48

## Context

The schema 1.47 transform-path slice established a typed, execution-aware terminal uppercase step. Reverse is the next deterministic text operation already present in the general graph runtime, and it can reuse that contract without introducing a generic expression language or a new scheduling model.

## Decision

Schema 1.48 adds `core.sequence.reverse` (`Sequence reverse`) with:

- required `in`/`out` string data pins;
- required `execute`/`then` execution pins;
- deterministic Unicode-scalar reversal;
- zero or more direct MCP predecessors;
- direct connection from the transform to the workflow Output;
- ordinary node lifecycle and Sequence lineage evidence.

The Studio exposes the node only in the schema-1.48 explicit picker and provides a guarded promotion from a valid schema-1.47 transform path. Existing schema 1.43, 1.46, and 1.47 definitions remain readable and unchanged. Invalid drafts remain saveable but cannot publish or run.

## Deferred

This decision does not admit arbitrary transform configuration, templates, numeric transforms, child WorkflowCall nodes, Agent-after-transform paths, branches, joins, loops, or expressions. Each requires its own typed activation, failure, data-availability, authority, evidence, playback, and migration review.

## Verification

Core validates the topology and promotion metadata, the production runner reverses a persisted path value and preserves Sequence output evidence, the Studio project compiles with schema-gated authoring controls, and the complete solution/documentation checks remain required before publication.
