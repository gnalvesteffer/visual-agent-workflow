# Blueprint-inspired contract inventory

This document records the stable contract boundaries identified by the Blueprint capability audit. It is an inventory of the current platform contracts and their ownership, not a promise that every Blueprint capability is implemented. New authoring features should extend these boundaries rather than introducing canvas-only state or a second runtime model.

## Contract map

| Contract | Stable identity | Portable definition | Host/runtime responsibility | Evidence and compatibility |
| --- | --- | --- | --- | --- |
| Workflow asset | `WorkflowId` | Schema version, entry node, nodes, data connections, optional execution connections, parameters, skills, profiles, collaboration metadata | Catalog persistence, actor authorization, draft/publish lifecycle | Canonical JSON and definition hash; legacy schema readers remain explicit |
| Revision | `RevisionId` plus parent revision | Exact normalized `WorkflowDefinition`; layout and source provenance are attached but not executable content | Publication, activation, immutable run admission | `WorkflowRevision` verifies the stored hash; runs retain revision and hash |
| Node | Dictionary key in `Nodes` | Type, `TypeVersion`, node configuration, optional origin and capability requirements | Validate, schedule, invoke capabilities, record node outcome | Node identity is retained in connections and execution events; origin is informational |
| Port | Node-local stable port ID | Port kind/direction/type are derived from node contract and configuration | Check multiplicity, type compatibility, execution/data rules | `WorkflowPorts` / `WorkflowValidator`; port IDs must not be inferred from position or label |
| Connection | Dictionary key plus source/target references | Source node/port, target node/port, and explicit `Kind` | Validate topology and schedule execution edges | Explicit execution connections are separate from data connections in schema 1.23+ |
| Parameter | Parameter dictionary key | Display name, default, description, type, bounded choices | Resolve invocation overrides and enforce host policy | Getter nodes expose values; invocation parameters are captured in run headers |
| Capability requirement | Binding/profile/skill/function identity plus contract hash | Requirement reference only; no endpoint, credential, or grant | Catalog resolution, authorization, approval, invocation, redaction | Package requirements and tool/profile/skill hashes detect drift; imports do not grant authority |
| Child workflow/function | Workflow revision or function ID/version/content hash | Pinned immutable definition and explicit typed mappings | Bounded expansion/evaluation, call scope, nested evidence | Package dependency closure and provenance preserve exact inputs |
| Layout | Workflow/node/edge presentation IDs | Positions, dimensions, comments, sections, reroutes, view metadata | Editor-only persistence and migration | Separate layout hash; layout cannot change executable content hash |
| Run | Run ID, revision ID, content hash | Captured definition, input, parameters, layout/provenance, event stream | Execution ownership, cancellation, controls, storage | Playback reads recorded events; it does not execute the definition or call live services |

## Current invariants

1. A portable definition contains requirements, not authority. Host grants, credentials, endpoints, approvals, and process policy remain outside the workflow contract.
2. The canonical definition hash excludes layout and is computed from normalized, schema-versioned JSON. Draft hashes are editor change markers and are not publication identities.
3. Node and port identities are explicit and stable. Display order, screen position, wire curvature, and creation order have no runtime meaning.
4. Execution wires activate nodes; data wires bind values. A data dependency does not implicitly schedule work.
5. A published revision is immutable. Changing a model/tool/skill catalog entry affects future admission or future runs, not the meaning of historical evidence.
6. Capability discovery is not capability authorization. Every runtime tool use is admitted by host policy and recorded under the relevant node/invocation identity.
7. Historical playback is passive. A recorded run can be inspected without credentials or re-invoking providers and tools.
8. Draft persistence is intentionally more permissive than publish/run validation. Incomplete topology may be saved, but it cannot be admitted as executable content.

## Gaps exposed by the inventory

These are implementation/design gaps, not silently supported features:

- Core now has a canonical node-family catalog and immutable resolved port descriptors that expose requiredness, connection cardinality, direct-MCP static-value fallback semantics, dynamic-pin identity sources, authored dynamic order/labels, transient discovery descriptions, and existing node-specific runtime limits; explicit-graph validators consume the descriptors for typed data and execution-output degree checks, and schema-neutral pin metadata conformance now applies to resolved dynamic projections. The descriptor layer still does not unify all editor affordances, schema-specific dynamic identity/migration, runtime enforcement, and evidence fields; `NodeTypes`, legacy `WorkflowPorts.Inputs`/`Outputs`, and validator branches continue to provide some of those pieces separately. See [dynamic pin descriptor resolution](decisions/0012-dynamic-pin-descriptor-resolution.md) and [dynamic descriptor conformance](decisions/0016-dynamic-descriptor-conformance.md).
- Port type IDs remain string-based for wire compatibility, but the supported graph set now has a versioned Core type catalog with exact-identity compatibility and no implicit conversions. Nullability, richer containers, explicit conversion nodes, and optional/default semantics still need future contracts.
- Mutable variables, events, macros/templates, interfaces, generalized retries, and timeline-like latent work lack the ordering, ownership, cancellation, and replay contracts required for Blueprint-level support. Mutable variables now have a reviewed run-scoped proof boundary, but remain deferred until the gates in [Decision 0027](decisions/0027-run-scoped-variable-proof-gate.md) are implemented and verified.
- Core now has an exact schema capability catalog for supported workflow versions and selected graph-mode flags, plus a deterministic inventory of individually named, non-executable promotion operations. The promotion inventory records observed source/target pairs without path finding or automatic transforms; historical loading, source-preserving migration, revision provenance, and package-form coverage remain outside it. See [schema promotion inventory](decisions/0015-schema-promotion-inventory.md).
- Core now provides an advisory, non-serialized host-admission report that compares persisted provider/tool requirements with a host snapshot. Node/call-path policy findings, broader provider/model contract identity, and execution-time enforcement remain host-owned follow-up work. See [advisory host-admission report](decisions/0025-advisory-host-admission-report.md).

## Required proof before extending the model

For each new Blueprint-inspired construct, add evidence for all of the following before marking it shipped:

- portable JSON definition and canonical hash behavior;
- stable IDs and explicit pin/type semantics;
- independent draft and executable validation;
- backend execution or pure evaluation without Studio;
- host authority and secret-handling boundary;
- recorded inputs, outputs, failures, cancellation, and nested identity;
- package/export/import and legacy compatibility behavior;
- editor discovery, diagnostics, undo, and accessibility behavior;
- passive playback or an explicit documented reason why playback is not applicable.

