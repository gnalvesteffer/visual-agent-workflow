# Decision 0082: Immutable record type (Blueprint-struct analogue)

**Status:** PROPOSED, deferred after three-role review (2026-11-24); not implemented. No slice is authorized until the prerequisites in "Review outcome" are designed. Prerequisite 2 (shared canonical encoder) is done as a no-behavior-change refactor; prerequisites 1 and 3-6 have PROPOSED designs below that are not accepted or implemented. See "Prerequisite status and proposals (2026-11-25)".

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

## Prerequisite status and proposals (2026-11-25)

Records remain deferred and unimplemented. This section records the one prerequisite that is now true in code and
the unaccepted design proposals for the rest. Every "PROPOSED" item below is a design candidate: no declaration,
`record<...>` type ID, node, validator rule, package field, or evidence shape described here exists.

### Prerequisite checklist

| # | Prerequisite | State |
|---|---|---|
| 1 | Declaration-resolution design | PROPOSED (P1); not accepted |
| 2 | Shared canonical encoder with golden-byte tests | **Done (implemented 2026-11-25).** |
| 3 | Mixed-class redaction policy | PROPOSED (P2); depends on Decision 0075, which is itself REVISE |
| 4 | Capability gate or format-version check | PROPOSED (P3); not accepted |
| 5 | Migration / revision-identity policy | PROPOSED (P4); not accepted |
| 6 | Evaluate schema-validated `json` first | PROPOSED comparison (P5); not accepted |

### Done: shared canonical value encoder (prerequisite 2)

`WorkflowCanonicalValueEncoding` (`src/VisualAgentWorkflow.Core/WorkflowCanonicalValueEncoding.cs`) is now the one
place that spells canonical primitives. `WorkflowArrayLiteral.TryNormalizeValue` calls it; output is byte-identical to
the previous inline code. Pinned rules:

- Numbers must be finite (`TryFormatNumber` returns false; `FormatNumber` throws for NaN or infinities). Zero,
  including negative zero, is `0`. Every other double uses `ToString("R", InvariantCulture)`, for example `1E+21`,
  `5E-324`, `1.7976931348623157E+308`, and `123456789012345678` becomes `1.2345678901234568E+17`.
- Integers are signed 64-bit decimal; values outside the range are rejected by the array parser, not the encoder.
- Booleans are `true` and `false`.
- Strings are written by `Utf8JsonWriter` with its default encoder: the output is ASCII, with non-ASCII and
  HTML-sensitive characters (`<`, `>`, `&`, `'`, `+`, `"`) escaped as uppercase `\uXXXX`. **No Unicode normalization is
  applied**: precomposed and decomposed e-acute produce different bytes and are different values. A record must not
  assume otherwise; adding normalization later would change existing array hashes and needs its own decision.
- No insignificant whitespace. `json` array elements keep their original tokens (for example `1.0`, `-0`) apart from
  whitespace; that is not the number rule and is not shared.

Verification: `WorkflowArrayCanonicalBytesGoldenTests` was written and run against the pre-extraction code (all but one
hand-guessed expectation passed; the guess was corrected to the observed value) and again after; the whole Core suite
passed unchanged. `WorkflowCanonicalValueEncodingTests` pins the encoder directly. Not extracted: other `"R"` call
sites (`WorkflowParameters`, `WorkflowVariableSnapshots`, `WorkflowRunner`, `WorkflowRunner.StructuredProof`) still
format numbers independently, mostly as display or prompt text. Whether they must converge is open and is not needed
for records, which would call the shared encoder directly. They use the same `"R"` format, but the shared zero
collapse is not applied to all of them, so folding them in could change bytes and was deliberately not done here.

### P1. Declaration resolution (PROPOSED)

**Problem.** `WorkflowDataTypes.IsSupported` and `AreCompatible` are pure string functions with about 21 call sites in
12 files. `record<id@rev>` is meaningful only relative to a declaration, so something must answer "does this type ID
exist, and what are its fields" without every caller needing a new argument.

**Candidates.**

| Option | Where the declaration lives | Pro | Con |
|---|---|---|---|
| A. Workflow-scoped list | `WorkflowDefinition.Records` (new optional, omitted when empty), part of the executable JSON and its hash | Self-contained; hash and package carry it; old hashes preserved when absent; export/import and playback need nothing external; matches how graph-function snapshots are embedded (schema 1.27) | Copy per workflow; two workflows with the same logical record have unrelated declarations; a called child workflow must expose its record types through its interface |
| B. Package or project library | Separate catalog item referenced by ID and revision | One definition shared by many workflows | Execution depends on an external lookup; the definition hash no longer pins behavior; reopen and passive playback need the catalog; breaks "published definitions are immutable and self-describing" unless the resolved declaration is also snapshotted |
| C. Interface-level only | Declared on a workflow interface (parameters and outputs) | Smallest | Cannot type internal pins or variables; useless for `make`/`get` |

**Recommendation: A, with B only as an authoring-time source that is copied into the workflow (copy-on-insert, as with
reusable pipelines and graph functions).** The definition stays self-contained, so run admission, playback and package
validation never consult a catalog. Cross-workflow identity (a record crossing a workflow-call boundary) is by
canonical declaration content hash plus the declared record ID, checked at the call node.

**Threading context without a global signature change.** Keep the existing string overloads for non-record types and
make them return false for any type ID with the `record<` prefix. Add one resolution step in the validator, run
admission, and graph-function/variable validators: an immutable `WorkflowRecordCatalog` built once from
`definition.Records` and passed to the few places that must answer record questions. Callers that only handle
primitives and arrays (most of the 21 sites) keep their current behavior and must treat an unresolved `record<...>`
as unsupported, so a missed call site fails closed rather than degrading to `json`. A test enumerates the call sites
and asserts each rejects a record type ID that has no catalog. This is a proposal: whether a missed site truly fails
closed must be proven per site before acceptance.

**Rejected here:** a process-global registry. It makes `IsSupported` context-free in name only and breaks
determinism across hosts.

### P2. Mixed-class redaction (PROPOSED)

**Grounding.** Decision 0075 is REVISE: approval payload privacy has no accepted lifecycle, and ordinary run evidence
today retains typed-array values in full, charged against the 65,536-byte evidence budget
(`WorkflowArrayEvidenceBudget`). The only existing redaction vocabulary is `payloadState = "redacted"` with
`inputCharacters` on direct-MCP `tool.authorized` events (`McpToolBroker`). There is no per-value or per-field
sensitivity class in the code today, and no type carries a class. The Direction text above ("fields inherit the
existing redaction classes of their types") therefore describes something that does not exist.

**Proposal.**

1. A record field has an explicit author-declared class: `public` (default) or `protected`. The class is part of the
   declaration, so it is hash-visible and revisioned. The type of a field never implies a class.
2. A record's effective class is the maximum over its fields. Any `protected` field makes the whole record value
   protected for evidence purposes: the record as a unit is summarized, never partially shown.
3. A protected record in evidence is recorded as `{ type, revision, payloadState: "redacted", canonicalBytes, hash }`
   where `hash` is a SHA-256 over canonical bytes salted per run, so equal values in different runs are not
   correlatable and low-entropy values (booleans, small integers) cannot be confirmed by hash alone. An unsalted hash
   would let anyone with a candidate value confirm it, and must not be used for protected fields.
4. A `public`-only record may retain per-field values within the existing budget. When over budget, summarize by type,
   revision and hash; never truncate mid-value (unchanged from the Direction).
5. `record.toJson`, MCP projection and any node that turns a record into text or JSON preserve the class: its output
   is protected if the record was. A protected record cannot feed an Approval-wrapped MCP call until Decision 0075's
   lifecycle is accepted.
6. Display names are never recorded in value evidence (unchanged).

**Open:** a hash does not let a reviewer see what ran; this proposal is consistent with 0075's statement that a hash
alone is not a substitute for exact-action review, so protected records need the 0075 protected-payload path before
they can be used in approvals. Until 0075 is resolved the first slice should reject `protected` fields instead of
inventing a lifecycle (all fields `public`, no per-field class), which makes the mixed-class question moot for the
first slice while keeping the declaration format room to add the class later.

### P3. Capability gate and format-version approach (PROPOSED)

**Grounding.** Repository policy and the version-compatibility skill treat schema 2.0 as the current additive
authoring identity, with no schema or capability gate per feature (the Wait slice, Decision 0081, row 23, adds none and
documents that fail-closed old-reader behavior is unbuilt). Old readers also ignore unknown event kinds. An older 2.0
build that meets an unknown node type fails in validation, but an unknown type ID string such as `record<...>` on a
pin must be checked, not assumed.

**Proposal.**

1. Add no new schema version. Records ship as additive 2.0 content: the optional `Records` list is omitted when empty,
   so every existing hash is unchanged (precedent: omitted approval and condition properties in schemas 1.4 and 1.5).
2. Each declaration carries `formatVersion` (integer, starting at 1). The reader rejects an unknown `formatVersion`,
   an unknown `record<...>` type ID, and any `core.record.*` node type it does not know, in validation, run admission,
   package import and passive playback, with a diagnostic naming the unsupported feature rather than "invalid".
3. Prove before accepting: characterization tests that feed a definition with an unknown type ID and node ID to the
   existing validator, run admission, SQLite and file loading, package import and playback of the current build, and
   record what each does. The Wait work found the assumed fail-closed behavior was false for events, so this must be
   measured first, not claimed.
4. Evidence for records uses new event payload shapes only inside existing event kinds, or a new kind named in
   `WorkflowWorkEvidence`, following the Decision 0081 rule. Do not add an `UnresolvedWorkKind` value.
5. If measurement shows an old reader silently degrades a record pin, add a minimum-reader marker to the definition (a
   required-capabilities list that older readers must reject when non-empty) as a deliberate, separately reviewed
   compatibility change. That is a policy departure from "no gate" and is not recommended unless the measurement
   demands it.

### P4. Migration and revision identity (PROPOSED)

Candidates: (a) integer revision in the type ID, as in the Direction; (b) content hash of the canonical declaration.

**Recommendation.** Keep `record<id@rev>` with a monotonic integer revision for authoring, and store the canonical
declaration content hash beside it as the identity checked wherever two workflows meet (workflow-call boundaries,
package import). The integer is for humans and ordering; the hash prevents two unrelated workflows that both say
`@3` from being treated as compatible. Within one workflow, ID plus revision is unique by construction.

Policy:

- Exact-revision-only compatibility: no implicit upgrade or conversion. Existing pins keep their revision.
- Field-ID tombstones are never reused; a revision bump is required for any add, remove, retype or default change.
- Migration is an explicit, validated, pure `record.migrate` node (or Studio action that rewrites pins into a new
  draft) generated from a field-ID mapping: carry over matching field IDs with identical types, require an explicit
  default for added fields, reject removed-or-retyped fields without an author-supplied conversion. It is not
  automatic, not part of the first slice, and published definitions are never rewritten (restoring or upgrading
  creates a new revision).
- Persisted values (for example variable checkpoints or recorded evidence) at an old revision are never reinterpreted;
  a checkpoint at a different revision is rejected, matching the Decision 0071 checkpoint rule.

Because revision-in-ID makes every field edit breaking, authoring must make "new revision" cheap (Studio rewrites all
pins in a draft atomically); without that the first slice stays unusable, which is the opponent's main objection and
is not resolved by this proposal.

### P5. Schema-validated `json` alternative (PROPOSED) and decision table

**Proposal (small).** Add an optional closed-object annotation to a `json` pin, variable or workflow parameter: a
bounded `JsonObjectShape` of field entries `{ fieldId, name, type }` where `type` is a primitive or
`array<primitive>`, all required, no extra keys, no null, depth 1, at most 32 fields. The wire type stays `json`
(so the 21 call sites and `AreCompatible` do not change); a shape is an additional validator constraint checked at
construction and at annotated boundaries. Add `core.object.get` selecting by `fieldId` and returning the typed
primitive. Values stay ordinary JSON text, canonicalized with the shared encoder (field-ID-sorted keys), so hashing
uses the same bytes. Shapes live on the pin or parameter in the workflow definition (option A of P1: nothing to
resolve). Renaming changes `name` only; the stored key is `fieldId`, which also gives rename-safety. No new type
family, no catalog entry, no dynamic-pin make node (an object is built with the existing JSON projection node, whose
output is checked against the shape).

Limits of this alternative: compatibility between two shaped `json` pins is a shape comparison rather than type-ID
equality, so exact-identity guarantees are weaker; revisions and migration do not exist (a different shape is a
different shape); redaction still needs P2's class rules; and nothing is "closed" at runtime outside annotated
boundaries because the value is still JSON.

| Criterion | Keep `json` | Shaped `json` (P5) | Records (this decision) |
|---|---|---|---|
| New type-ID family and catalog change | none | none | yes |
| Touches the ~21 `IsArrayType` sites | no | no | yes |
| Declaration resolution (P1) | none | none (inline on pin) | required |
| Rename-safe typed field access | no (pointer by key) | yes (by `fieldId`) | yes |
| Closed object, unknown fields rejected | no | yes at annotated boundaries only | yes everywhere |
| Exact-identity compatibility | n/a | shape equality, weaker | strong |
| Dynamic-pin `make` node | n/a | not needed (projection) | needed |
| Migration / revision story | n/a | none (shape is the contract) | required (P4) |
| Old reader without feature | works | unknown annotation ignored: constraint silently dropped unless gated (P3) | must fail closed (P3) |
| Canonical bytes reuse | n/a | shared encoder | shared encoder |
| Evidence/redaction (P2) | per JSON, none | needs P2 | needs P2 |
| Implementation risk | none | low to medium | high |

**Observation, not a decision.** An old reader ignoring an unknown pin annotation is the sharpest risk of the
alternative, since it would silently drop the closed-object guarantee while still running. The annotation therefore
needs the same P3 unknown-field rejection as records; that makes it cheaper than records but not free. The
recommendation of this proposal set is: evaluate shaped `json` first (it needs P3 and P2's public-only subset, but not
P1, P4 or the ~21-site audit); revisit records only if exact-identity types across workflow boundaries, a typed
`make` node, or migration prove necessary. Neither alternative is accepted, and both should go through a fresh
three-role review before any code.

## Shaped json first slice (2026-11-25)

Status: **implemented** (Core, runner, tests, docs). This is the smallest slice of the P5 alternative. Records remain
deferred and unimplemented. The three-role review that P5 asks for before accepting either alternative was not run for this
slice; the slice was scoped by the delegating brief to the narrowest carrier and treated as a reversible, additive change.

### Old-reader measurement (P3, measured first)

`UnknownNodeAnnotationTests` characterizes the current Core reader, in the style of `UnknownEventKindTests`. The measurement was
taken before any shape code existed, using the real future names (`inputShape` on a node and `core.object.get`), then the names
were swapped for ones that stay unknown:

- `WorkflowJson` reads definitions with `UnmappedMemberHandling = Disallow`. An unknown optional property on a node, or on a
  nested annotation object, throws `JsonException` in both `Deserialize` and `DeserializeDraft`. **Nothing is silently dropped,
  so the forward-compatibility hazard that P5 feared does not exist for definitions.** This is the only place a
  `WorkflowDefinition` is deserialized (the other `Disallow` readers cover layouts, graph functions, interfaces, templates, the
  clipboard, dependency closures and draft promotion).
- An unknown node type fails validation (`Deserialize` and `DeserializeDraft` throw; `NodeTypes.IsSupported` is false).

**Compatibility decision: no schema or capability gate.** An older 2.0 reader rejects a definition that uses the annotation or
the node, with a parse or validation error, rather than running it without the constraint. Gating would add a version for no
extra safety. The cost is that the failure message is the generic unmapped-member or unsupported-node error, not one that names
the feature; a named diagnostic is not built. A later test (`ShapedJsonPersistenceTests`) sends a shaped definition through
draft read/write, strict read/write, package export and import, the file run journal and the SQLite run store: both
`inputShape` and `objectGet` survive and the content hash is identical on every path. Not measured: a current reader
against an unknown annotation inside the stores and package import (they share the `WorkflowJson` readers, which reject it in
the unit tests); the design catalog (`SqliteWorkflowCatalog`) publication path; and playback of a run whose stored
definition an old reader cannot parse (historical runs are unaffected, since no existing definition uses the feature).

### What shipped

| Piece | Where | Behavior |
|---|---|---|
| `JsonObjectShape` / `JsonObjectFieldShape` | `WorkflowJsonObjectShape.cs` | `fields: [{ id, name, type, required }]`, all four JSON properties required on read; flat; types `string`, `integer`, `number`, `boolean` only. |
| `NodeDefinition.InputShape` | `WorkflowDefinition.cs` | Optional, omitted when null, so every existing definition hash is unchanged. Valid only on a schema 2.0 `core.input` with `inputDataType: "json"`. |
| `core.object.get` | contracts, port catalog, validator, runner | Pure, data-evaluation node. Input `in` (json), output `out` typed by `objectGet.dataType`. Dynamic pin identity kind `ObjectFieldType`. Schema 2.0 only. |
| Boundary validation | `JsonObjectShapes.ValidateValue`, runner `RequireShape` | Rejects non-object, duplicate key, undeclared key, missing required key, wrong type (null, `3.5` for integer, string for boolean), non-finite number, over 16,384 characters, depth over 32. Runs when the shaped Input executes and when its pure value is first resolved. |
| Validator | `WorkflowValidator.cs` | Shape well-formedness (1 to 32 fields, unique valid IDs, supported types, name 1 to 128 characters); an `objectGet` must name a field of the directly connected shaped Input with the same type. Incomplete connections are draftable and never publishable. |

Design choices and why:

- **Carrier: the schema 2.0 `json` Input node.** It is the one place where a caller-supplied value enters a graph, it already
  has a json-typed boundary and a `typeVersion`, and the property is omitted when absent. The other carriers named by P5
  (Output, workflow parameters, variables, function ports, workflow-call fields) each need their own admission and
  compatibility story and are left out.
- **Fields are addressed by `id`, which is also the JSON key.** `name` is display-only: renaming it changes the definition hash
  but not any node, edge or accepted value (`Renaming_a_field_display_name_does_not_change_wiring_or_validity`).
- **`core.object.get` stores the field type, and the validator proves it equals the shape.** Pin types therefore derive from the
  node alone, like `ArrayElementType`, with no graph-context lookup in the port catalog. The cost is that changing a field's
  type in a shape fails validation of every reader node until each is updated.
- **The source must be the shaped Input directly.** Shape-to-shape compatibility through other nodes is the weakness the table
  below lists; this slice does not try to solve it.
- **Optional fields exist but are weak.** `required: false` permits omission at the boundary; reading an omitted field fails
  the node with `Field 'x' is absent from the object.` There is no default or presence test yet.
- **Fail closed for a shape the reader does not understand.** A missing or extra shape-field property, an unknown field type
  such as `record<x@1>`, `object` or `array<string>`, or an unknown node property is rejected on read; there is no downgrade
  to plain `json`.
- A missing `required` property on a shape field is a read error (`[JsonRequired]`), not an implicit `false`.

### Decision table update (against P5)

| Criterion | Shaped `json` as built | Change from P5 |
|---|---|---|
| New type-ID family, catalog change, ~21 `IsArrayType` sites | none touched | as predicted |
| Declaration resolution (P1) | inline on the Input; nothing to resolve | as predicted |
| Rename-safe typed field access | yes, by `id` | as predicted |
| Closed object, unknown fields rejected | yes, at the shaped Input only | as predicted |
| Exact-identity compatibility | none; `object.get` binds to the Input directly | weaker than predicted: no shape equality check exists |
| Typed `make` node | none; build with JSON projection, whose output is not checked against any shape | as predicted |
| Migration / revision | none; changing a field type breaks readers at validation | as predicted |
| Old reader without feature | **rejected on read, not ignored** | **better than P5 predicted; no gate needed** |
| Canonical bytes | no new encoding; the value is plain JSON text, key order is the caller's | P5 suggested field-ID-sorted keys; not done, so equal objects can hash differently |
| Evidence and redaction (P2) | values are recorded as ordinary typed data evidence; no class | unchanged; do not feed protected data through a shaped Input |
| Implementation size | one Core file, one validator block, one runner case, no new schema version | low |

### What remains

1. Review: the fresh proponent, opponent and adjudicator review that P5 requires has not been run.
2. Studio: no palette entry, shape editor or inspector. A shape can only be authored in JSON. `WorkflowGraphCanvas`,
   `WorkflowEditor` and the Function editors have no label or icon for `core.object.get`.
3. Other carriers: Output, workflow parameters, variables, function ports, workflow-call interface fields, MCP projection.
4. Checking a JSON projection's output against a shape, and shape-to-shape compatibility (this is what would make an object
   passable between nodes and workflows).
5. Canonical key order and hashing of values; nesting; arrays and `json` fields; defaults and presence tests for optional fields.
6. A named "unsupported feature" diagnostic for older readers, if the generic rejection proves too opaque.
7. Revisit records only if exact-identity types across workflow boundaries, a typed `make` node, or migration prove necessary.

### Review follow-up (2026-11-25)

- **Editing.** Changing a shaped Input away from json (editor type picker, or removing a JSON selector that collapses the Input
  to text) now clears `InputShape` (`GraphEditing.SetBoundaryType`, shared by the editor). Before, the leftover shape raised the
  non-draftable `object.shape.unexpected` and the edit threw. Covered by `ShapedJsonEditingTests`. The Blazor picker itself was
  not driven in a browser.
- **Untrusted text.** Caller key names in boundary errors are cut to 64 characters plus an ellipsis, and an unpaired surrogate
  escape in a key or string value is reported as a boundary error instead of an unhandled `InvalidOperationException`.
- **Loop frames (pinned limitation).** Inside a For Each body, a shaped Input's pure value is resolved from the frame's current
  input, which is the loop item, so the item text is validated against the shape and the pass fails. Read a shaped Input only
  outside loop bodies. Pinned by `A_shaped_input_read_inside_a_for_each_body_validates_the_loop_item_not_the_run_input`.

