# Decision 0063: Workflow interface contract foundation

**Status:** Accepted; bounded Core interface-call runtime support and project-owned revision storage added (2026-11-02). Studio authoring and call binding remain deferred.

## Decision

Add a framework-independent, versioned interface asset contract in Core. An
interface has a stable ID, a display name and description, and 1–32 named
operations. Schema 1.0 operations declare one typed workflow-boundary input
and output. Schema 1.1 operations retain JSON workflow boundaries and declare
1–16 required named fields for each direction. Each field has a stable ID,
display name, and workflow data type. The interface asset has its own
`schemaVersion`, canonical JSON form, and content hash, independent of
workflow schema 2.0.

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

For schema 1.1, Core's `WorkflowInterfaceFields` adapter packs every required
input field into a deterministically ordered JSON object and projects a child
JSON object into declared output values. It rejects missing, duplicate, or
undeclared properties and mismatched JSON types. A field typed `json` accepts
any JSON value, including explicit JSON `null`; absent properties remain an
error. Adapters are bounded to 1 MiB and JSON depth 32. No coercion, defaults,
optional fields, or implicit conversions are supported.

The contract contains no prompts, tools, credentials, grants, endpoints, or
authority. A hash and a pinned revision establish content identity only; they
do not authorize execution. Schema 2.0 now has a `WorkflowInterfaceCall` node
that resolves one operation from an embedded implementation revision, exposes
typed data pins for every named field, packs incoming values into bounded JSON,
executes the pinned child workflow once, and projects its JSON output onto the
named result pins. Nested lifecycle events reuse pinned workflow-call evidence
and passive playback. Package requirements, run dependency closure, host
admission, descriptor auditing, and the run inspector include interface and
child revision identities.

Studio project storage now persists immutable interface and implementation
revisions. Catalog-backed projects export/import them in source snapshots;
folder projects index `.interface` and `.implementation` revision files in
manifest format 1.3. The folder Explorer currently displays these assets as
read-only. Typed asset editors, implementation binding controls, dependency
diagnostics, and graph-editor call selection are not yet available, so
interfaces are still not authorable end to end in Studio. The call does not
grant child workflows model or MCP access. See [Decision 0064](0064-workflow-interface-asset-storage.md).

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
- Schema 1.1 fields round-trip canonically; packing and projection reject
  missing/extra fields, duplicate JSON properties, and wrong JSON types, while
  preserving explicit `null` for arbitrary JSON fields.
- Core validates schema-2.0 interface-call pins from the exact operation
  signature, and package/dependency manifests include interface,
  implementation, and child revision identities.
- An Agent Framework regression connects typed pins to an interface call,
  executes the exact embedded JSON workflow once, reads multiple projected
  output fields, and reconstructs nested-call evidence through passive
  playback.
- Host permissions remain outside both portable definitions.

## Deferred gates

Before calling interfaces shipped, add Studio authoring for interfaces,
implementations, operation selection, and pin configuration, then complete
dependency diagnostics and browser verification. Add distinct playback events
only if the existing nested-workflow evidence proves insufficient. Never
resolve an implementation through a mutable catalog at run time.
