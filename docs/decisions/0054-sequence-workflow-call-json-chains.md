# Decision 0054: Bounded two-node JSON composition chains

## Status

Implemented as schema 1.54 Core, runtime, migration, and authoring coverage. This is a
deliberately bounded Blueprint-style typed-dataflow increment, not a general downstream
topology contract.

## Contract

Schema 1.54 preserves the schema 1.53 JSON WorkflowCall forms and adds only these data chains:

```text
call.out → Output.in
call.out → JsonSelectString.in → Output.in
call.out → JsonProject.in → Output.in
call.out → JsonProject.in → JsonProject.in → Output.in
call.out → JsonProject.in → JsonSelectString.in → Output.in
```

The child has JSON input and output boundaries. There are at most two pure JSON nodes after
the call. A projection can feed another projection or a terminal selector; a selector cannot
feed another node. The execution barrier remains `WorkflowCall.out → Output.execute` and JSON
nodes are data-only. Fan-out, sharing, reconvergence, cycles, effects, MCP/tools, Agents,
branches, loops, and implicit conversion remain rejected.

## Compatibility and migration

The explicit `sequence-workflow-call-json-chains` migration promotes a valid schema 1.53
definition to 1.54 without adding nodes or rewriting edges. Node IDs, connection IDs, child
revision/content identity, layout, and source lineage are preserved; the target revision is
new and records its parent through migration provenance. Existing 1.53 behavior remains
unchanged until the explicit migration is selected.

## Runtime and evidence

The existing recursive explicit-data evaluator is reused only after the dedicated 1.54
topology analyzer has accepted the graph. Intermediate projection values stay typed JSON;
the terminal output serializes JSON or returns selected text. `data.evaluated` evidence records
each evaluated node and connection in the dependency chain. Child failure, cancellation,
unknown outcome, and successful no-result termination do not activate the data chain.

## Deferred

Arbitrary pure-node chains, multiple consumers, JSON-to-Agent composition, post-call external
effects, and general multi-output/result contracts require separate activation, authority,
failure, replay, and migration decisions.

## Verification

Core covers the schema catalog, dedicated topology acceptance/rejection, migration provenance,
and graph identity preservation. Agent Framework coverage executes Project→SelectString and
asserts both data-evaluation evidence records. Existing schema 1.53 tests remain separate.
