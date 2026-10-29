# Decision 0050: Bounded public Sequence WorkflowCall slice

**Status: Core/runtime, draft-promotion, catalog/package, and guarded Studio promotion slices implemented; direct Sequence-path authoring remains deferred.**

## Context

The private WorkflowCall barrier proof established the lifecycle, settlement,
playback, tamper, and host-admission invariants needed for a narrow public
contract. The public contract must still be smaller than general Blueprint
composition: it must be linear, bounded, and independently reviewable.

## Decision

Schema 1.50 is an additive explicit-graph schema. A Sequence output may contain
zero or more direct MCP tool nodes followed by one terminal WorkflowCall pinned
to an embedded string-input/string-output child revision, or it may remain an
ordinary direct-MCP path. Every path is disjoint and ends at the shared Output
barrier. The call leaves through `out`, connects directly to Output, and carries
the exact child identity tuple: workflow ID, revision ID, release version, and
content hash. Recursive child admission and provider grants remain enforced by
the existing Core admission boundary.

The existing migration registry accepts only valid revisions. Its explicit
1.49 → 1.50 operation is schema-only and preserves the source revision,
content hash, layout, and migration provenance. An invalid call-bearing draft
must not be represented as a `WorkflowRevision` or silently accepted by that
registry. A separate draft-candidate/promotion operation is required before
Studio can promote such a draft; it must carry an immutable canonical draft
envelope, generation, diagnostics, and a tamper-evident marker, and it must
validate the resulting 1.50 revision before publication.

The normal runner now validates and executes schema 1.50 through the same public
entry point as other persisted revisions. The production planner uses the
shared topology analyzer and preserves nested lifecycle evidence and Sequence
barrier settlement. No proof-only bypass is used by this path.

The invalid-draft boundary is implemented separately from valid-revision
migration. A schema-1.49 call-bearing draft is saved with its generation,
canonical definition/layout bytes, hashes, and validation diagnostics. Studio
can explicitly promote that draft; promotion recomputes the diagnostics and
requires the resulting schema-1.50 revision to pass strict validation before
the catalog publishes it. SQLite stores the promotion provenance in schema 5,
and packages export the source envelope in format 1.2. This provenance is
historical evidence and an integrity boundary, not an authorization grant.
The Studio does not yet author the WorkflowCall directly into a Sequence path;
that remains the next bounded authoring increment.

## Verification

Core validates valid and malformed call-bearing topologies, exact child pins,
ordinary MCP sibling compatibility, and the explicit 1.49 → 1.50 migration.
Agent Framework tests execute a serialized two-path schema 1.50 revision
through `ExecuteWithOutcomeAsync` and verify both nested child completions and
the parent completion event. Draft-candidate promotion, catalog persistence,
package round trips, and the guarded Studio promotion action are covered by
Core, Hosting, and Studio regressions. Direct Sequence-path call
insertion/editing and the broader Blueprint topology remain roadmap work.

