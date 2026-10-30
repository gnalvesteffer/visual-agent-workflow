# Decision 0058: bounded JSON-array predicate routing

## Status

Accepted and implemented in schema 1.57.

## Context

Schema 1.56 can produce a deterministic integer array length, but it cannot use
that value to select an execution route. A general data-driven control system
would introduce expression evaluation, arbitrary fan-out, mutable state, and
new replay and failure semantics. This increment therefore admits one fixed,
typed predicate with an explicit false outcome.

## Decision

Schema 1.57 admits exactly:

```text
WorkflowCall.out -> JsonToArray.in -> ArrayLength.in -> IntegerEquals.left
IntegerConstant.out -> IntegerEquals.right
IntegerEquals.out -> BooleanBranch.condition
ArrayLength.out -> Output.in

WorkflowCall.out -> BooleanBranch.execute
BooleanBranch.true -> Output.execute
BooleanBranch.false -> Terminate.execute
```

The constant is a signed 64-bit authored value. The true route returns the
array length as an integer. The false route uses a failure `Terminate`; it
does not produce a result. Existing schema-1.56 array graphs remain valid after
explicit 1.56→1.57 schema promotion without inventing predicate nodes. The
Sequence collector settles descendant failure termination before the parent
closes.

Authoring adds the comparison, constant, Boolean Branch, and failure
termination as one guarded draft edit. Broader predicates, arbitrary control
edges, successful no-result data routes, loops, mutation, and general
expressions remain outside this slice.

## Evidence

Core tests cover the schema catalog, migration inventory, strict topology
validation, draft authoring, and validator admission. Existing Agent Framework
regressions cover the production Sequence collector and WorkflowCall runtime;
the exact control route remains bounded by the fixed topology analyzer.
