# Decision 0051: One deterministic transform after a Sequence WorkflowCall

## Status

Implemented as the additive schema 1.51 Core/runtime and Studio authoring slice. Broader
post-call composition remains future work.

## Contract

Schema 1.51 preserves every valid schema 1.50 graph and adds one narrow path shape:

`direct MCP* → pinned string WorkflowCall → exactly one Sequence uppercase or Sequence reverse → Output`

Paths remain bounded, linear, disjoint, and execution-connected. The existing schema 1.50
analyzer remains unchanged; schema 1.51 has a dedicated analyzer so admitting the transform
cannot weaken the terminal-call contract.

The execution transport distinguishes an absent child result from an actual empty string. A
transform runs only when the child completed successfully with an available string result. A
child failure, cancellation, uncertainty, or successful no-result termination follows the
existing terminal disposition and does not create transform lifecycle events. An available empty
string is still a real value and is transformed normally.

Adding a transform is an explicit draft edit. The draft remains saveable at schema 1.50 with
diagnostics, then must be promoted to schema 1.51 before strict serialization, publication, or
execution. The forward migration from a valid 1.50 graph to 1.51 is schema-only and preserves
the graph when no transform is present.

## Verification

Core covers topology, catalog, migration, and draft boundaries. Agent Framework covers a
serialized schema 1.51 call followed by reverse and verifies the transformed result. The public
runner carries result availability through the private execution message without changing the
public `WorkflowExecutionResult` constructor compatibility.

## Deferred

Two transforms, transforms before calls, Agents, MCP after calls, branches, loops, parallel
composition, shared descendants, and arbitrary coercion are not part of this slice.
