# Decision 0078: nested bounded Loops

**Status:** Accepted and implemented in unified schema 2.0.

**Date:** 2026-11-23

## Context

The unified-schema Loop contract admitted one bounded Loop per workflow. Core
already had a non-authoritative descriptor-derived topology proof that could
identify nested feedback ownership, but public validation and the ordinary
Agent Framework runner did not admit nested Loops. Supporting nesting requires
each Loop to maintain its own iteration while carrying the enclosing activation
frame, and requires a root-owned work bound so nested limits cannot multiply
without limit.

## Decision

Unified schema 2.0 admits statically nested predicate Loops without a schema,
capability, or nesting-depth flag. Core's normal validator proves one body
anchor and one feedback return per Loop, checks unique structured ownership,
removes only proven feedback edges for acyclic validation, and rejects
Loop/Fork/Join combinations when a workflow has more than one Loop. Existing
single-Loop Fork/Join composition remains supported under Decision 0077.

The runner resolves the current Loop's iteration from its own activation frame,
not the enclosing Loop's iteration. On `done`, it restores the parent frame and
parent iteration before sending the downstream activation. Nested predicate,
failure, and cancellation behavior follows ordinary node execution; a failed or
cancelled inner pass does not settle outer Loop completion.

Every Loop retains its 1–16 per-node body-pass bound. An atomic budget caps
total body-pass activations at 1,024 across all Loops in one workflow
invocation. Before dispatching body pass 1,025, execution fails and records the
aggregate limit as the workflow failure reason. A separate per-invocation cap
of 65,536 Loop-scoped evidence events bounds journal growth; events outside
Loop scopes do not consume that budget. Pinned WorkflowCall descendants receive
independent limits. Root-wide accounting across child calls remains open.

## Verification

Core tests cover valid two-level ownership and malformed feedback escapes.
Ordinary public-runner unit tests cover nested iteration identity and scope,
aggregate pass exhaustion, failure and cancellation propagation, and concurrent
atomic work/evidence reservations. Tests use deterministic local executors; no
browser, integration, external model, or MCP server is part of this proof. Full
suite validation caught a failure when sharing the pass-budget object across
child WorkflowCall runners, so this implementation keeps child limits
independent.

## Limits

Nested Loops cannot yet be combined with Fork/Join in the same workflow.
Collaboration, Break, and typed-array iteration are not added by this decision.
The per-invocation pass and evidence caps are operational bounds, not author
configuration fields. Historical schema definitions and their execution
semantics are unchanged.

See [bounded loop graphs](../loop-graphs.md), [architecture](../architecture.md),
the [Blueprint capability matrix](../blueprint-capability-matrix.md), and the
[roadmap](../roadmap.md).
