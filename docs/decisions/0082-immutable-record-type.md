# Decision 0082: Immutable record type (Blueprint-struct analogue)

**Status:** PROPOSED, deferred after three-role review (2026-11-24); not implemented. No slice is authorized until the prerequisites in "Review outcome" are designed.

**Date:** 2026-11-24

## Context

Confirmed current behavior:

- `WorkflowDataTypes` defines `string`, `boolean`, `integer`, `number`, `json`, `array<json>` and `array<T>` for primitive `T`; `WorkflowDataTypeCatalog` lists each with a `TypeVersion` of `1.0.0`, and `AreCompatible` requires exact type-ID equality with no implicit conversion (`src/VisualAgentWorkflow.Core/WorkflowDataTypes.cs`).
- Arrays are bounded at 64 items, depth 32, and 16 KiB (`WorkflowJsonArrayLimits.cs`, `WorkflowArrayLiteral.cs`). Operations return new values and do not mutate (`WorkflowTypedArrayOperations.cs`).
- Equality exists only for primitives (`WorkflowPrimitiveValueEquality.cs`); it throws for containers.
- The roadmap defers records, maps and sets "after stable field identity and portable key/equality/order rules are demonstrated". The capability matrix marks typed values and variables "Partial, bounded". [Decisions 0066-0072](0066-unified-typed-array-values.md) cover arrays.

Today a structured value must be `json`, which is untyped and addressed by JSON Pointer. A pointer path names a field by its display key, so renaming a key silently breaks every consumer. This is the gap an Unreal struct fills without Unreal's object model.

Everything below is a proposal.

## Options

1. **Keep `json` only.** No new surface, but no named, typed, rename-safe fields.
2. **Immutable `record` (proposed).** Declared fields with stable IDs and typed pins.
3. **Full struct parity with maps and sets.** Rejected for now; see "Deferred".

## Direction

**Declaration.** A workflow, package or project owns a `record` declaration. It has a stable record ID, a monotonically increasing record revision, and an ordered list of fields. Each field has a stable field ID (opaque, generated once, never reused), a mutable display name, a type, and an optional default. Every field is required. There is no null or optional field in the first slice, matching `SupportsNull=false` in the catalog.

**Type identity.** The type ID is `record<recordId@revision>`. Compatibility is exact-identity, the same rule as `AreCompatible`. A different revision is a different type, and nothing converts implicitly. Studio may offer an explicit, validated migration node or action, but that is out of scope here.

**Rename and compatibility.**

- Changing a display name does not change the revision or the canonical bytes.
- Adding, removing or retyping a field, or changing a default, bumps the revision.
- Removed field IDs are tombstoned and never reused.
- Existing pins keep their pinned revision, so upgrading is an explicit edit.

**Allowed field types.** The allowed types are `string`, `boolean`, `integer`, `number`, `json`, `array<primitive>`, `array<json>` and one level of nested `record`. Nesting is limited to depth 2. A record may not reference itself, directly or transitively. `array<record>` is a separate staged step (see Proof plan).

**Bounds.** The limits are 32 fields per record, 16 KiB canonical size (the existing array payload cap), and `json` or array fields bounded by their existing limits. The sum is checked after construction, not per field. The 65,536-byte per-invocation evidence budget from Decision 0071 applies unchanged.

**Canonical serialization and hashing.** The canonical form is a JSON object keyed by field ID, not display name. Keys are sorted by ordinal UTF-8 byte order. It uses the existing canonical encodings: integers as signed 64-bit, numbers as finite doubles with the repo's current number-spelling rule, no insignificant whitespace, and strings as UTF-8 with the existing escaping rules. The declaration serializes with the same key ordering. Package and revision hashes include the declaration, so renames are hash-visible while values are rename-stable. A byte-identical round trip through workflow and project packages is a gate.

**Nodes (all pure; no execution pins).**

- `core.record.make`: one input pin per field, keyed by field ID, plus a `record` output. All fields must be connected or defaulted.
- `core.record.get`: a `record` input, with the field chosen by field ID, and one typed output.
- `core.record.with` is optional and deferred to its own slice. It takes a record and one field, and returns a new record.

Validator rules:

- Both endpoints of a record wire must pin the same revision.
- A field ID must exist in the pinned revision.
- A pin type must equal the declared field type.
- A missing required field is a diagnostic, not a runtime error.
- A declaration cycle is rejected.

**Difference from bounded JSON.** JSON is untyped and path-addressed, and it preserves numeric token spelling. A record is closed, so unknown fields are rejected, and it is typed per field, addressed by stable ID, and the schema is versioned. A record has no null, no extra keys, and no pointer addressing. Converting between the two is an explicit node (`record.toJson`, `json.toRecord`) with strict validation. Neither conversion is implicit.

**Equality and ordering.** None in the first slice. Records cannot be compared, used with `array.contains`, or sorted. `WorkflowPrimitiveValueEquality` stays primitive-only. A later proposal may add field-wise equality over primitive fields only, but nothing here depends on it.

**Evidence and redaction.** The data-evaluation and variable evidence budgets apply. Redaction is per field ID, and fields inherit the existing redaction classes of their types. Display names are not recorded in value evidence, so a rename cannot change historical evidence. Over-budget records are summarized by type, revision and hash, never truncated mid-value.

**Catalog, versioning and older readers.** Add a catalog entry for the record family with `IsContainer=true`, `SupportsNull=false` and `SupportsImplicitConversion=false`. Record declarations carry an explicit format version. An older reader that sees an unknown record type, declaration version or node ID must fail closed in validation, run admission and passive playback. It must never degrade to `json`. There is no schema or capability gate; this ships in unified schema 2.0 as additive behavior.

**Interactions.**

- *Variables.* Run-scoped record variables follow Decision 0071: canonical default, exact Get/Set pins, versioned evidence, and checkpoint rejection.
- *`array<record>`.* This reuses the array operations once the record canonical form is proven. Contains and sort stay unsupported.
- *Graph-function ports.* Exact-identity ports, as in Decision 0069.
- *Direct-MCP argument pins.* A record may be projected to a declared closed-object argument only through an explicit record-to-JSON node. Approval-wrapped calls stay excluded per [Decision 0075](0075-approval-payload-privacy.md).

## Deferred: maps and sets

Maps and sets need portable key identity, canonical key ordering, equality, hash and collision rules, and deterministic iteration. Float and string normalization are unresolved, and none of these rules is demonstrated yet. Records need none of them, because fields are addressed by ID and canonical order is the sorted ID order. If records prove stable, they supply the evidence the roadmap asks for.

## Evidence required (staged proof plan)

1. Core unit tests first. Cover canonical bytes and hash determinism, rename invariance, revision bump rules, bounds and cycle rejection, and exact-identity compatibility. Add a validator matrix for make/get pins and an older-reader fail-closed test.
2. Pure make/get through the public runner, including recorded evidence and redaction.
3. File and SQLite reopen and passive playback, and workflow and project package round trips preserving bytes and hashes.
4. Studio declaration authoring with browser verification of rename, add and retype diagnostics.
5. Record variables, then graph-function ports, then `array<record>`, then the optional `with` node, then MCP projection.

## Open questions

1. Should field IDs be author-supplied or generated? Is a revision integer sufficient, or is a content hash needed for cross-package identity?
2. Where does a declaration live (workflow, project library, or both), and how do packages resolve and pin it?
3. Is depth-2 nesting with 32 fields and 16 KiB the right first bound? Should `json` be permitted as a field type at all?
4. Is an explicit migration node required in this slice, or is an exact-revision-only policy acceptable?
5. Should `with` ship in the first slice or stay separate?
6. Is no equality at all acceptable, or is field-wise primitive equality the minimum for usability?
7. Which redaction class should a record inherit when its fields have mixed classes?
8. Is fail-closed with no schema or capability gate sufficient for older readers, or is an explicit declaration-format version check also needed in package import?

## Review outcome (2026-11-24)

Three-role review (proponent, opponent, adjudicator). **Ruling: defer; do not ship staged proof steps 1-2 as proposed.** Record the prerequisites below and revisit. The proponent's slice is sound in shape, but its central premise, that `record<id@rev>` is "one more parsed family", is false in this codebase.

### Claims verified (read-only)

- **True:** the catalog is static and string-matched. `WorkflowDataTypeCatalog.All` is a fixed array, and `IsSupported` and `AreCompatible` are pure string checks with no context argument (`WorkflowDataTypes.cs:57-77`). A record type depends on a user declaration, so a resolution context must be threaded through every caller, which is a signature change, not a parser addition.
- **True:** about 21 `IsArrayType`/`IsPrimitiveArrayType` call sites exist across 12 files (Validator 6, GraphFunctions 2, VariableEvidence 2, VariableSnapshots 2, and one each in WorkflowRunner, LoopVariableTopology, CheckpointFrames, WorkflowDefinition, WorkflowGraph, TypedArrayOperations, VariableEditing). The audit is real and the count matches the proponent's.
- **True:** node contracts are static with a `TypeVersion` check and a `HasDynamicPins` flag (`GraphEditing.cs:803-807`; `GraphClipboard.cs:144`). `core.record.make` needs per-declaration pins, so it is a dynamic-pin node and hits those paths.
- **True:** the array byte cap is 16 KiB, while `WorkflowJsonArrayLimits.cs` itself holds only the item count (64) and depth (32). A 32-field record of `json` or array fields could exceed 16 KiB unless the sum is checked after construction, which the draft already specifies. The opponent's 512 KiB figure is a worst case that the post-construction sum check would reject, so it is an argument for the check, not a blocker.
- **False (overstated):** "no `-0` canonical double rule". `WorkflowArrayLiteral.cs:124` emits `"0"` when `number == 0d` (which covers -0) and otherwise uses `ToString("R", InvariantCulture)`. A rule exists but lives in array code, is not a pinned cross-type contract, and has no test tied to record hashing. It needs promotion to a shared, documented encoder, not invention.
- **Not verifiable as stated:** "2.0 already shipped, so a capability gate is needed". The roadmap treats 2.0 as the current authoring identity (`roadmap.md:44-48`), and the draft's "no gate" claim contradicts the fail-closed requirement for older 2.0 readers. This stays an open prerequisite.

### Why defer, not the alternatives

- Revision-in-type-id makes every field edit a breaking change with no migration (draft open question 4), which makes the first slice hard to use.
- Declaration location (open question 2) decides whether the type ID can be resolved at all, so it cannot be left open.
- Mixed-class redaction (open question 7, Decision 0075) is a privacy risk, so it must be settled before evidence is recorded.
- The opponent's schema-validated `json` alternative is not adopted. It is a separate design that needs its own decision (a closed-object annotation on pins and variables plus `object.get` by field ID). It is noted here as the cheaper candidate to evaluate first, and it does not need a new type family.

### Prerequisites before any slice

1. A declaration-resolution design: location (workflow, project or both), package pinning, and how `IsSupported`/`AreCompatible` and descriptor lookups receive that context. This includes a plan for the ~21 call sites and dynamic-pin node contracts.
2. A shared, pinned canonical encoder for double (including -0), int64 and UTF-8 strings, extracted from `WorkflowArrayLiteral`, with golden-byte tests.
3. A field-class redaction policy for mixed-class records (extends Decision 0075).
4. A capability gate or explicit declaration-format version check for older 2.0 readers.
5. A migration or revision-identity policy (content hash versus integer revision).
6. Evaluate the schema-validated `json` annotation alternative against records using these same criteria.

### First slice, once prerequisites 1-4 are accepted

Flat-field records only: generated field IDs, declaration plus revision, `record<id@rev>` with exact identity and no null or implicit conversion, canonical bytes and hash sorted by field ID, pure `core.record.make` and `core.record.get`, validator rules, and fail-closed handling for unknown record types or nodes. Core unit tests and a validator matrix come first. Nesting, `array<record>`, `with`, variables, function ports, MCP projection, migration and Studio UI stay deferred.

The roadmap and capability matrix already describe records as a proposal only, so neither contradicts this ruling and neither was changed.
