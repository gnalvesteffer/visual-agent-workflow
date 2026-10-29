# Advisory node descriptor conformance audit

**Status: Implemented, non-authoritative**
**Scope: Blueprint-inspired node metadata and pin projections**
**Decision date: 2026-10-28**

The Core layer now exposes an advisory audit for the canonical node descriptor
projection. It compares the descriptor with the existing configured data and
execution pin projections, the capability projection, and descriptor invariants.
The audit can cover the static descriptor registry or one safe default instance
of every canonical node family. A test-only overload accepts a descriptor so
intentional drift can be detected without changing the registry or a workflow.

This is a maintenance and regression boundary, not a new authority boundary:
the audit is not serialized, does not affect content hashes, does not validate
workflow drafts, does not grant host capabilities, and is not called by runtime
dispatch or schema admission. Existing schema-specific validators remain the
authority for persisted definitions.

The audit is intentionally conservative. It reports disagreement between
metadata consumers, but it does not infer richer Blueprint semantics such as
conversion, defaults, pin promotion, or execution guarantees. Those require
their own compatibility and runtime evidence contracts.

## Follow-up gates

- Keep the public Sequence-to-Agent path deferred until host admission,
  multi-descendant settlement, persisted nested identity, and passive replay are
  proven under a new additive schema (currently expected to be 1.46).
- Extend the audit only when a new authoritative consumer or node-family
  projection is added; do not turn it into a runtime dependency.
- Add persisted Sequence 1.43 failure, cancellation, uncertainty, host-denial,
  package-round-trip, and tamper-rejection fixtures before revisiting that
  promotion.
