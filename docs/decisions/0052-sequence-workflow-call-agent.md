# Decision 0052: One terminal Agent after a Sequence WorkflowCall

## Status

Implemented as the additive schema 1.52 Core, package, migration, and runtime slice.
Studio authoring for this topology remains a follow-up and is not implied by schema support.

## Contract

Schema 1.52 preserves every valid schema 1.51 graph and adds one bounded path shape:

`direct MCP* → pinned string WorkflowCall → (Output | one deterministic text step | one terminal plain-text Agent) → Output`

Paths remain linear, disjoint, acyclic, and capped. The Agent has exactly one string input and
one string output, a pinned provider contract, finite static limits, and no tools, skills,
delegates, graph-function tools, routes, or structured-result mode. A transform and an Agent
cannot both follow the same call in this slice. Child provider capabilities and grants remain
independent; the parent does not authorize or inherit child capabilities.

The normal runner passes an available child string, including an empty string, through the
ordinary `WorkflowCall.out` edge to the Agent. A successful child termination without a value,
failure, cancellation, or uncertain outcome bypasses the Agent and preserves the existing
terminal disposition. Scoped child lifecycle evidence, execution activation/transfer evidence,
Sequence lineage, and provider-contract requirements remain durable for passive replay.

The explicit migration from valid schema 1.51 to 1.52 is schema-only and preserves node,
connection, and revision identity. Invalid drafts remain saveable but cannot be published or
executed until the dedicated analyzer and ordinary graph validation pass.

## Verification

Core covers topology diagnostics, schema catalog, migration, validator admission, and package
provider-contract requirements. Agent Framework covers a serialized schema 1.52 graph with a
pinned child result reaching a real terminal Agent and completing the parent. Existing 1.50 and
1.51 call behavior remains covered by their regression suites.

## Deferred

Agents with tools or delegation, multiple post-call Agents, post-call MCP nodes, branches,
loops, parallel composition, shared descendants, structured results, and Studio authoring are
not part of this bounded slice.
