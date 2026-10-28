# Static Boolean Branch descriptor

## Decision

The typed Boolean Branch node is the first control-family node promoted into the
Core static descriptor registry. Its descriptor owns the existing explicit-graph
pin shape:

- one required Boolean data input, `condition`;
- one required execution input, `execute`;
- two required execution outputs, `true` and `false`.

`WorkflowPorts.Explicit`, `ExplicitDataInputs`, `ExplicitDataOutputs`,
`ExplicitExecutionInputs`, and `ExplicitExecutionOutputs` all project this
descriptor. The descriptor is authoring metadata only: schema admission,
Boolean evaluation, route selection, runtime dispatch, and recorded evidence
remain in their existing boundaries.

## Compatibility boundary

This does not alter serialized workflow definitions, node type versions,
content hashes, schema promotion behavior, or legacy condition/merge/loop
paths. Input and Output, legacy Condition, Terminate, Merge, Loop, and LoopBody
remain outside the static registry because their effective pins vary by schema,
configuration, or legacy graph contract. Dynamic Agent, MCP, parallel,
Sequence, workflow-call, and graph-function pins remain resolver-owned.

The descriptor registry must not be treated as a schema admission list. A
descriptor can describe a node shape even when the active schema validator
rejects that node.

## Proof

Core tests validate the descriptor metadata and assert that every explicit pin
projection returns the same IDs, order, direction, kind, and type. Existing
workflow validation and runtime route tests continue to cover schema gates and
Boolean execution semantics.
