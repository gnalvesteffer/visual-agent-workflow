# Decision 0071: typed arrays in run-scoped variables

**Status:** Accepted design; implementation in progress; proof gates remain open
**Date:** 2026-11-15  
**Scope:** Blueprint parity workstream; unified schema 2.0

## Context

Unified schema 2.0 supports immutable `array<string>`, `array<boolean>`,
`array<integer>`, and `array<number>` values through literals, workflow
boundaries, parameters, and pure graph functions. Run-scoped variables still
hold scalar values only. The capability matrix identifies array variables as a
remaining type-system gap.

The required independent proponent/opponent/adjudicator review agreed that
whole-array storage alone would be too limited to count as useful array-variable
support. Authors need a bounded way to update arrays from values produced during
execution, while preserving explicit state mutation, deterministic dataflow,
ordered variable writes, and passive playback.

## Decision

Extend the existing run-scoped variable contract to the four primitive typed
arrays, with whole-value replacement and immutable transformation nodes. Keep
all capabilities in the unified schema; do not introduce a schema gate,
promotion step, or preview mode.

- Variable declarations retain their stable IDs, names, descriptions, and
  non-secret classification. An array declaration names its primitive element
  type and provides a JSON-text default validated against that exact type.
- `Get variable` exposes the exact declared `array<T>` type and reads the value
  at its explicit activation point. `Set variable` accepts only that exact type
  and atomically replaces the complete value in one ordered, versioned write.
- Pure `append`, `insert-at`, `replace-at`, `remove-at`, and `concatenate`
  operations return new `array<T>` values. They never mutate a variable. A
  workflow persists a transformed value only through an explicit `Set`.
- Arrays remain ordered, homogeneous, non-null, and limited to the existing
  64-item and 16,384 UTF-8-byte JSON-value bounds. Empty arrays are valid when
  their element type is declared. Numeric items must be finite. No implicit
  type conversion, nested arrays, maps, sets, or reference values are added.
- Indexes are zero-based. Insert accepts `0..Length`; replace and remove accept
  `0..Length-1`. Negative and out-of-range indexes fail at the responsible
  transformation node. Concatenation requires exact element-type identity and
  preserves left-to-right order. Every result is revalidated against both
  array bounds.
- Successful writes advance the variable version once. A failed parse,
  transformation, or write leaves the existing value and version unchanged.
  Cancellation after a committed write does not roll back the write.
- Preserve current variable ownership and ordering: workflow-local stores are
  isolated per run and per child invocation; only the existing linear and
  bounded-loop regions may use them. Parallel forks, Sequence, collaboration,
  asynchronous continuations, graph functions, shared parent/child state, and
  checkpoint recovery remain unsupported until separately proven.
- Defaults, state values, and playback evidence use canonical compact JSON for
  arrays. The per-value limit remains 16,384 UTF-8 bytes. Before implementation
  is admitted, define and enforce an aggregate byte budget covering array
  transformation evidence and variable read/write evidence, including the
  deterministic failure point when the budget is exhausted. Evidence needed to
  reconstruct committed state must never be silently truncated.

## Required proof before marking this shipped

1. Validate typed declarations/defaults, exact Get/Set pins, malformed JSON,
   nulls, wrong item types, integer overflow, non-finite numbers, empty values,
   and item/UTF-8 boundaries.
2. Verify append, insert, replace, remove, and concatenate ordering, index
   boundaries, exact type identity, maximum-size results, and deterministic
   node-attributed failures.
3. Prove failed writes do not change the value/version; prove ordered reads and
   writes through linear execution and bounded loops; prove run and child-call
   isolation and cancellation before/after committed writes.
4. Define and enforce the aggregate evidence budget, then prove variable state
   can be reconstructed from every event prefix without re-evaluating nodes.
   Prove snapshot/checkpoint admission rejects unsupported array state.
5. Prove unsupported execution regions are rejected by independent validation,
   not left to scheduler ordering.
6. Preserve existing scalar variable definitions and behavior, `array<json>`
   behavior, unified-schema definitions, package round trips, hashes, and
   historical playback.
7. Expose declarations and transformations in Studio with typed pins and
   invalid-draft persistence. Update architecture and user guides.

## Compatibility

This is additive to unified schema 2.0. Existing scalar definitions, typed
array definitions, legacy `array<json>`, historical schemas, immutable
revisions, and recorded playback retain their existing contracts. Loading a
workflow never silently rewrites authored array values or graph definitions.
No capability is considered shipped until validation, runtime, evidence,
compatibility, Studio behavior, and focused automated regression coverage agree.

## Implementation progress

The initial implementation adds typed array declarations and JSON defaults,
exact Get/Set pins, pure immutable transformation nodes, canonical run-scoped
storage, linear and bounded-loop execution, event-prefix playback, checkpoint
snapshot rejection, and a 65,536-byte per-invocation budget over canonical array
values recorded by data evaluation and variable read/write evidence. Focused
Core and Agent Framework checks cover validation, atomic write rejection,
canonical evidence, transform-to-Set execution, loop order, and file/SQLite store
reopen. Studio browser verification covers declaration creation and editing,
valid and invalid default save/reopen, Problems diagnostics, and run blocking.
Invalid folder drafts are excluded from callable interface workflow choices;
strict package and run validation still reject them. Runner-backed bounded-loop
and file/SQLite reopen tests reconstruct typed-array state at every captured
event prefix without execution. Focused cancellation tests prove that canceling
before an array write emits no variable state and canceling after it preserves the
committed array for playback. The full Agent Framework suite passed 644/644 on
2026-11-16; both cancellation cases also pass. Failure-prefix and full historical
compatibility proofs remain open.

## Review record

The proponent favored whole-value replacement using existing run-scoped
ownership and bounds. The opponent showed that replacement alone cannot
accumulate or combine runtime-produced values and proposed immutable operations.
The adjudicator selected the combined contract above, adding concatenation to
the minimal transformation set, and retained the existing concurrency,
checkpoint, and evidence proof gates.
