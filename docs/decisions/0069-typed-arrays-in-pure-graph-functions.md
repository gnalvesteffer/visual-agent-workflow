# Decision 0069: Typed arrays in pure graph functions

**Status:** Implemented in schema 2.0
**Date:** 2026-11-02
**Scope:** Typed primitive-array ports, values, evaluation, and evidence in pure graph-function snapshots

## Context

Decisions 0066–0068 establish bounded immutable `array<T>` values in ordinary
graph dataflow, workflow boundaries, and parameters. Pure graph functions remain
the next placement: their named ports and embedded snapshots currently admit
only scalar types, while their deterministic evaluator has a separate node
allowlist. Port typing alone would be insufficient; direct workflow calls and
Agent graph-function tools share the same snapshot evaluator and must accept the
same value contract.

The required proponent/opponent/adjudicator review recommended proceeding with
both call surfaces and the existing pure array operations, subject to an
aggregate value/evidence bound and runtime input validation. Per-array limits
alone do not constrain repeated input/output/evaluation evidence.

## Decision

Schema 2.0 pure graph functions accept exactly `array<string>`,
`array<boolean>`, `array<integer>`, and `array<number>` ports. Port and data-edge
identity is exact. `array<json>`, nested arrays, null elements, effectful or
nested functions, iteration, mutation, variables, workflow parameters, and
implicit conversions remain unsupported inside function bodies.

Function bodies may use the existing Array Literal, Array Length, and Array Get
nodes. Their element-type configuration must agree with the connected
`array<T>` value. Values use a cloned `JsonElement` containing canonical JSON,
which is immutable and independent of caller-owned document lifetimes. Every
input is checked against its declared port and the shared array parser before
evaluation. The existing per-array bounds remain 64 items and 16,384 UTF-8
bytes; numbers must be finite and integers must fit signed 64-bit range. Array
Get remains zero-based and out-of-range errors identify the inner function node
as well as the enclosing function call.

One logical function invocation has a **65,536-byte aggregate array-value
budget**. It counts canonical UTF-8 JSON bytes for array inputs retained in
call evidence, every array value retained in evaluated-value evidence (including
repeated cached-value entries), and array results retained in outputs. Direct
workflow calls share the budget across all output pins evaluated for one call
node; one Agent graph-function tool call shares it across all declared outputs.
The evaluator charges values before adding them to retained evidence or
serializing the completion event, and rejects an invocation that would exceed
the limit. This budget is separate from existing node-visit, tool-call, and
per-array limits. Existing aggregate character limits on Agent graph-function
arguments and results continue to apply.

Function definitions remain embedded, versioned snapshots. Their existing
serialized representation and SHA-256 function hash are unchanged for scalar
functions; array port types and literal configuration naturally participate in
the hash. Workflow packages continue to carry exact function snapshots and
requirements. Runtime never consults the mutable authoring catalog. Data and
Agent tool events retain the exact function identity, typed inputs, outputs,
and bounded evaluated values; historical playback reads recorded events and
does not invoke the evaluator or a catalog.

Typed-array function placement is admitted only when the calling workflow uses
the unified schema 2.0 graph. Existing historical workflow admission and
function hashes remain unchanged. Function assets and pure templates may use
the same types in current authoring. No feature-specific promotion or preview
gate is added.

## Verification

- Core tests validate each port type, exact type edges, supported array
  operators, malformed or over-bound inputs, function hashes, package round
  trips, and unchanged scalar-function hashes.
- Evaluator tests cover all four array types, empty arrays, literal/length/get,
  exact input shape, array and aggregate bounds, canonical values, cache/evidence
  retention, and inner-node failure attribution.
- Runtime tests execute embedded snapshots through direct workflow calls and
  Agent graph-function tools without catalog lookup and verify bounded typed
  values and exact snapshot evidence in the emitted run events. Playback reads
  those recorded events without re-invoking the evaluator or catalog.
- Historical callers reject typed-array functions while retaining their
  existing scalar behavior; unsupported effects, nested functions, mutation,
  iteration, and `array<json>` remain rejected.
- Studio's catalog and folder function editors expose the four array port types
  and the existing pure array nodes, with the normal function validation/save
  feedback.
