# Decision 0047: Keep Sequence-to-WorkflowCall as a proof-only seam

**Status: Proof-only; public schema 1.50 is not implemented.**

## Context

The bounded Sequence schemas through 1.49 deliberately stop at direct MCP nodes and deterministic text transforms. A child workflow call has a substantially larger contract than another scalar processing node: it pins an immutable child revision and content hash, creates a scoped nested lifecycle, can terminate without a value, can fail or cancel, and must settle the parent Sequence output before a parent barrier may complete.

Promoting the node by changing the validator or picker before those contracts are proven would make a visually authorable graph executable without reliable evidence, migration, or passive playback semantics.

## Decision

Keep public schema admission, Studio promotion, package compatibility, and persisted topology unchanged at 1.49. Add only an internal production-runner proof harness that constructs a deliberately non-admitted parent definition and exercises the existing pinned child-workflow executor.

The proof covers:

- a Sequence output invoking an exact embedded string child revision;
- parent-scoped `subworkflow.*` lifecycle evidence and child node evidence;
- child failure settling the Sequence output before the parent reports failure;
- successful child termination settling the Sequence output without a parent completion or downstream result;
- normal successful child output completing the parent.

The proof-only revision factory bypasses validation and serialization only for this internal harness. It is not reachable from workflow files, schema catalogs, migrations, Studio controls, package import/export, or runtime admission of user-authored definitions.

## Progress after the initial proof

Core now owns a framework-independent `SequenceWorkflowCallTopology` contract. It recognizes only call-bearing paths with a direct-MCP prefix, one terminal pinned string WorkflowCall, and the Output barrier; it rejects malformed pins, sharing, cycles/incomplete paths, transforms or other nodes around the call, and invalid child bindings. Public Sequence validation consumes its diagnostics while still rejecting the unsupported WorkflowCall node under schema 1.49, and the proof runner checks the same result before execution. Non-call sibling paths remain governed by their existing schema-specific contract.

The current production proof now dispatches two real pinned child-workflow descendants concurrently. A deterministic child boundary holds the first child while the sibling completes, and the parent remains incomplete until the held child is released. Core also owns a per-call settlement state machine that rejects duplicate and late outcomes, and nested lifecycle events emit separate child workflow/revision/content-hash evidence without changing the existing raw child result payload. The proof is not yet the required barrier qualification: child cancellation/uncertain outcomes, persisted file/SQLite replay, capability denial, and migration compatibility remain open.

## Deferred public gates

Schema 1.50 may be considered only after a reviewed implementation provides all of the following:

1. one authoritative Sequence-path call-boundary topology state machine, with valid and invalid topology tests;
2. exact child revision/workflow/hash validation, recursive child admission, and rejection of transforms, Agents, branches, joins, loops, sharing, cycles, and multiple calls in the bounded path;
3. success, failure, cancellation, successful no-result termination, and uncertain-outcome semantics;
4. sibling barrier ordering, cancellation, out-of-order completion, and no premature parent completion;
5. parent/call/child/Sequence lineage evidence in file and SQLite journals, passive playback without live execution, and tamper rejection;
6. package compatibility plus an actual migration registry and older-runtime behavior;
7. child capability and host-grant admission tests;
8. Studio authoring and user documentation that expose only the reviewed contract.

Until those gates are complete, any Sequence path containing a workflow call remains an invalid but saveable draft and must be rejected at publish/run boundaries.

## Verification

`SequenceProductionRunnerTests` covers the proof's success, child failure, and successful termination paths. The public schema 1.49 validator and catalog remain unchanged and continue to reject workflow calls in Sequence paths.
