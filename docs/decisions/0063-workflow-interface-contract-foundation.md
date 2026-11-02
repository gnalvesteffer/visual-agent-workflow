# Decision 0063: Workflow interface contract foundation

**Status:** Accepted for a Core proof slice; authoring and execution remain deferred (2026-11-02)

## Decision

Add a framework-independent, versioned interface asset contract in Core. An
interface has a stable ID, a display name and description, and 1–32 named
operations. Each operation declares one typed workflow-boundary input and
output. The interface asset has its own `schemaVersion`, canonical JSON form,
and content hash, independent of workflow schema 2.0.

An implementation revision binds every declared operation to an exact
immutable workflow revision snapshot and records its own revision identity,
version, and content hash. Conformance checks validate interface and workflow
hashes, reject missing or undeclared operation bindings, and check boundary
types. The current workflow boundary supports one payload input and one result
output; multi-pin operation signatures are not implied by this contract.

Core statically resolves an operation ID from an exact implementation revision
to its declared signature and exact workflow pin. The proof scenario uses a
string-to-string `lookup` operation and a JSON-to-JSON `search` operation,
each mapped to a different immutable workflow revision. An unknown operation
fails resolution; no runtime call node consumes this selection yet.

The contract contains no prompts, tools, credentials, grants, endpoints, or
authority. A hash and a pinned revision establish content identity only; they
do not authorize execution. The Core types do not add an asset catalog, Studio
authoring surface, package dependency closure, interface-call node, runtime
dispatch, playback events, or host admission. Interfaces therefore remain
unavailable to workflow authors and runners in this increment.

## Rationale

Interfaces are a separate reuse boundary from child workflow calls, pure graph
functions, and authoring templates. Their signatures should be immutable and
portable, while implementations stay pinned to exact workflow revisions.
However, the current workflow boundary exposes only one input and one output,
so this foundation must not imply full multi-operation implementation or
callable dynamic dispatch. A Core-only proof lets identity, conformance, and
wire behavior be tested before adding storage, package closure, UI, and runtime
semantics.

## Verification contract

- Canonical interface and implementation revision JSON round-trip with stable
  hashes and strict schema/duplicate-property validation.
- Conformance accepts multiple operation IDs with different payload types,
  each mapped to its own exact workflow revision; explicit resolution returns
  exactly the requested operation's pin and rejects an unknown operation.
- Missing and extra bindings, mismatched boundary types, and altered content
  hashes are rejected with deterministic diagnostics.
- Host permissions remain outside both portable definitions.

## Deferred gates

Before calling interfaces shipped, separately prove immutable asset storage
and source ownership, package dependency closure, authoring/editing UX, a
version-pinned call representation, runtime dispatch and evidence, host
admission and cancellation, and passive playback. Revisit whether operation
boundaries need named multi-pin inputs/outputs before publishing an authoring
contract. Do not resolve implementations through a mutable catalog at runtime.
