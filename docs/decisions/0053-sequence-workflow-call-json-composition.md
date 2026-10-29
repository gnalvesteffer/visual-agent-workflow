# Decision 0053: One bounded JSON composition step after a Sequence WorkflowCall

## Status

Implemented as the additive schema 1.53 Core, Agent Framework, and bounded Studio authoring
slice. Studio can add a new direct JSON child path from a clean schema 1.52 direct-MCP draft
and append one existing JSON selector or projection node.

## Contract

Schema 1.53 preserves valid schema 1.52 graphs and adds a narrow typed-result shape:

`direct MCP* → pinned JSON WorkflowCall → Output`

The call's child workflow has JSON input and JSON output boundaries. The parent execution path
still enters Output directly; optional data-only composition is expressed separately as either
`call.out → JsonSelectString.in → Output.in` or
`call.out → JsonProject.in → Output.in`. A selector requires a string Output and a projection
requires a JSON Output. At most one composition node belongs to a call path. JSON nodes are not
execution nodes, arbitrary JSON chains and implicit coercions remain rejected, and post-call
Agents, branches, loops, and parallel composition are not part of this slice.

The child result is recorded as a JSON runtime value for the parent's explicit data evaluator,
so output evaluation and `DataEvaluated` evidence use the same typed value as ordinary JSON
nodes. JSON output is serialized at the public string-result boundary. Existing schema 1.52
Agent paths remain valid after promotion when no JSON child contract is present.

The explicit 1.52-to-1.53 promotion is schema-only and preserves node, connection, and revision
identity. It does not invent a JSON node or silently change a caller's input. Invalid drafts
remain saveable but cannot be published or executed.

## Verification

Core covers catalog admission, promotion metadata, validator compatibility, pinned JSON child
contracts, direct data composition shape, rejection of string child boundaries, and guarded
authoring edits that create a new output or replace one direct data edge without reinterpreting
existing paths. Agent Framework builds and the existing sequence/runtime suite passes; richer
multi-node JSON dataflow remains a follow-up.

## Deferred

Multiple JSON composition nodes, arbitrary typed chains, JSON-to-Agent transitions, post-call
MCP nodes, branches, loops, parallel fan-out, implicit JSON/string conversion, and runtime
authoring controls remain deferred until each has its own topology, lifecycle, authority,
evidence, replay, and migration review.
