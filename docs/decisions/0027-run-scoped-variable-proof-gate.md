# Decision 0027: run-scoped variable proof gate

**Status:** Planned design boundary
**Date:** 2026-10-28
**Scope:** Blueprint parity roadmap; no shipped schema or runtime behavior

## Context

Blueprint-style mutable variables are the largest remaining gap in the current
authoring model. Workflow parameters are immutable run inputs, and explicit
node outputs do not provide named state that can be read and updated across a
linear graph or loop. Adding `Get` and `Set` nodes without defining state
ownership would make canvas order, executor scheduling, cancellation, and
playback disagree.

The required adversarial review was completed with independent proponent,
opponent, and adjudicator roles. The review agreed that the broad capability is
not implementation-ready, but that a bounded proof can proceed after the
contracts below are documented and tested.

## Decision

Keep mutable variables **deferred and out of the palette** until a separate
schema and runtime proof is complete. The proposed first slice is deliberately
narrow:

- variables are workflow-local and run-scoped; they are never project-global,
  host-global, shared memory, object references, or authority-bearing values;
- declarations have stable IDs, display names, descriptions, scalar types,
  bounded defaults, and non-secret classification;
- the initial scalar set is string, Boolean, signed integer, and finite number;
- explicit typed `Get variable` data nodes and `Set variable` execution nodes
  are the only access path;
- each workflow-call invocation receives an isolated variable store;
- linear execution and the existing bounded loop may use variables only after
  their read/write order is specified;
- parallel forks, Sequence paths, collaboration regions, asynchronous
  continuations, graph functions, and child-workflow sharing reject variable
  writes in the first slice;
- a committed write is not rolled back by later failure or cancellation, and
  variable workflows do not claim checkpoint/resume support until state is
  persisted atomically with the checkpoint.

The first proof should use a new schema version and preserve all pre-variable
definition hashes and behavior byte-for-byte. Variable reads must not use the
ordinary pure-data memoization path: a read observes the store at an explicit
activation point and records the observed write version.

## Required proof gates

Before promotion to a shipped capability, the implementation must prove:

1. declaration/default validation, stable IDs, scalar bounds, deterministic
   serialization, package round trips, and legacy hash preservation;
2. missing-variable, duplicate-ID/name, invalid-default, type-mismatch, and
   malformed-pin diagnostics;
3. default reads, ordered overwrite visibility, loop iteration semantics,
   failure, and cancellation before/at/after a committed write;
4. rejection of variable writes in concurrent or unsupported regions, rather
   than relying on lock acquisition or completion order;
5. isolated child calls and isolated simultaneous runs;
6. bounded `variable.get` and `variable.set` evidence containing variable ID,
   scope/invocation identity, iteration or branch identity where applicable,
   observed/write version, and bounded value or redaction metadata;
7. passive playback and prefix scrubbing from evidence without invoking model,
   tool, or workflow execution;
8. checkpoint admission/rejection and explicit retry/unknown-outcome behavior;
9. editor rename, delete, undo, copy/paste, invalid-draft persistence, and
   dangling-reference behavior.

## Explicitly deferred

Concurrent writes and merge policies, containers, arrays/maps/sets, object
references, secrets, implicit agent/tool access, shared parent-child state,
macros with mutable state, event subscriptions, latent/timeline work, durable
resume, rollback/transactions, and compensation remain separate decisions.

This gate prevents a visually convincing but semantically ambiguous Blueprint
feature from becoming a persisted workflow contract.
