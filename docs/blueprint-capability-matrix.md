# Blueprint capability matrix

This matrix is the audit baseline for the [Blueprint-inspired authoring contracts and ergonomics](roadmap.md#planned-phase-blueprint-inspired-authoring-contracts-and-ergonomics) phase. It compares the capability target in the [Unreal Blueprint reference](unreal-blueprint-reference.md) with the current Visual Agent Workflow implementation. “Shipped” means the repository contains a versioned contract, backend behavior, and focused verification; a palette item or a visual card is not evidence by itself.

The target is capability parity at the level of visual programming, not Unreal API or runtime compatibility. The matrix deliberately separates implemented behavior, bounded/partial behavior, future work, and concepts that are intentionally different because workflow authority and evidence have different requirements.

## Matrix

| Capability | Current evidence | Status | Next proof or decision |
| --- | --- | --- | --- |
| Versioned assets and immutable revisions | `WorkflowDefinition`, revision storage, package validation, pinned child workflow calls, and exact-revision run identity. See [reusable workflows](reusable-workflows.md) and [project workspaces](project-workspaces.md). | Shipped, bounded | Inventory asset identity, dependency closure, hashes, and migration rules in one contract document. |
| Named graphs and explicit node identity | Stable workflow/node/port IDs, separate layout, graph editor persistence, and explicit execution connections from schema 1.23 onward. See [graph editor](graph-editor.md). | Shipped | Prove canonical round-trip and identity preservation across package export/import and edits. |
| Execution versus data pins | `WorkflowPortKind`, explicit execution/data port enumeration, typed connections, runtime validation, and the versioned static pure-value descriptor slice. See [execution and data edges](decisions/0002-execution-and-data-edges.md) and [static pure pin descriptors](decisions/0008-static-pure-pin-descriptors.md). | Shipped, bounded | Extend the descriptor contract to dynamic/configuration-derived pins with explicit migration and compatibility rules. |
| Typed values and conversions | String, Boolean, integer, finite number for direct MCP pins, bounded JSON selection/projection, typed function ports, explicit graph-value nodes, and a versioned Core type catalog with exact-identity compatibility. See [graph values and functions](graph-values.md) and [finite number decision](decisions/0006-finite-number-primitive.md). | Shipped, bounded | Decide the next container types only after nullability, bounds, serialization, and conversion contracts exist. |
| Context-sensitive node discovery | Searchable node picker and pin-aware filtering in Studio; imported definitions still go through Core validation. | Shipped, bounded | Add browser proof for drag-from-pin filtering and an explicit diagnostic when a compatible conversion is unavailable. |
| Parameters and exposed defaults | Workflow-owned typed parameters, descriptions, choices, run overrides, getter nodes, and recorded invocation values. Schema 1.41 number propagation is contract-gated but not yet shipped. See [workflow parameters](workflow-parameters.md) and [finite number propagation](decisions/0007-finite-number-propagation.md). | Shipped, bounded | Prove finite numeric defaults/overrides and typed getter evidence without changing legacy parameter behavior. |
| Mutable variables | No general Set/Get variable system; current values are immutable parameters or explicit node outputs. | Target, not shipped | Design ordering, loop visibility, parallel writes, cancellation, evidence, and replay before implementation. |
| Pure functions | Pure graph-function definitions with typed ports, bounded evaluation, content hashes, catalog entries, and agent assignment support. See [graph values and functions](graph-values.md). | Shipped, bounded | Prove extraction/provenance/version upgrade behavior from a selected graph region. |
| Child workflows and composition | Exact pinned child revisions with parameter mappings, bounded expansion, provenance, and scoped calls. See [nested workflows](nested-workflows.md). | Shipped, bounded | Keep callable workflows distinct from authoring templates/macros and document dependency upgrades. |
| Macros/templates | No graph-fragment expansion or macro asset contract. | Target, not shipped | Choose expansion versus runtime call semantics, identity/provenance, dependency closure, and audit behavior. |
| Interfaces | No general interface asset or implementation-contract node. | Target, not shipped | Define typed operation signatures without granting credentials, tools, or host authority. |
| Events and custom entry points | Input is the workflow entry point; approval, collaboration, and host observation provide explicit bounded interactions. | Partial | Decide whether event subscriptions are needed and define ownership, lifetime, delivery, cancellation, and durable evidence. |
| Branching and bounded loops | String/Boolean routing, bounded loop, termination exits, named 2–8 branch regions, collaboration rendezvous, and explicit joins. See [conditional graphs](conditional-graphs.md), [loop graphs](loop-graphs.md), and [parallel graphs](parallel-graphs.md). | Shipped, bounded | Expand only through separately reviewed contracts for nested/repeated control and failure compensation. |
| Latent/time-based work | Provider/tool calls, approvals, bounded waits, and optional deterministic checkpoint recovery exist; unbounded ticks/timelines do not. See [pause and resume](run-control.md) and [checkpoint recovery](checkpoint-recovery.md). | Partial, intentionally bounded | Define durable wait identity, timer authority, restart behavior, and evidence before timeline-like nodes. |
| External capabilities | Node-scoped MCP/skill bindings, host-authorized model profiles, approvals, container workers, and isolated extensions. See [agent capability libraries](agent-capability-libraries.md). | Shipped, host-owned | Keep grants/credentials out of portable definitions and prove denial, redaction, and audit correlation. |
| Agent/tool argument contracts | Direct MCP nodes support schema-derived typed input pins and explicit static arguments; agent tool assignment is versioned and bounded. | Shipped, bounded | Complete a tool-schema compatibility matrix for optional arguments, JSON values, defaults, and runtime errors. |
| Failure, cancellation, and retry | Explicit failure outcomes, cancellation evidence, approvals, host cancellation, and bounded resource controls exist; generalized retries/compensation do not. | Partial | Define node failure dispositions and retry idempotency before adding recovery nodes. |
| Debugging and evidence | Recorded inputs/outputs, route and invocation evidence, event filtering, run inspection, passive playback, and checkpoint/reconciliation contracts. See [architecture](architecture.md) and [run reconciliation](run-reconciliation.md). | Shipped, bounded | Map every authored node/port identity to evidence and document live-control versus historical-playback boundaries. |
| Save/validate/publish lifecycle | Invalid drafts can be saved; validation gates publish/run; published revisions are immutable. | Shipped | Add compatibility tests for legacy loading, migration, invalid drafts, and publish/run rejection. |
| Editor organization | Comments, sections, reroutes, layout persistence, minimap/camera controls, resizable panes, themes, and keyboard-accessible graph interactions. | Shipped, bounded | Measure responsiveness and accessibility for large graphs and keep layout separate from executable semantics. |
| Collaboration | One bounded shared-channel rendezvous and a two-phase variant with exact grants and passive playback. See [parallel agent collaboration](parallel-agent-collaboration.md). | Shipped, bounded | Keep nested collaboration, retries, and broad multi-user editing as separate reviewed phases. |
| Unreal object/class model | No Actors, Components, inheritance, replication, physics, or engine object lifetime. | Deliberately different | Use workflow assets, host-owned capabilities, explicit calls, and versioned contracts instead of importing Unreal's world model. |

## Reading the status column

- **Shipped**: implementation and focused tests/documentation exist in this repository.
- **Shipped, bounded**: a useful subset exists with explicit limits that remain part of the contract.
- **Partial**: adjacent behavior exists, but the Blueprint capability is not a general supported model.
- **Target, not shipped**: the reference and roadmap describe the direction; it must not be presented as available.
- **Deliberately different**: parity would conflict with the workflow platform's host authority, portability, or evidence model.

## Audit rules

1. A new node type is not a completed capability until its definition, ports, validator, backend execution, persistence/versioning, evidence, editor behavior, and tests agree.
2. A visual affordance must not imply runtime support that the backend does not provide.
3. A new stateful or asynchronous feature requires explicit ownership, bounds, cancellation, restart behavior, and replay evidence before it is added to the palette.
4. Host permissions, credentials, endpoints, and approvals remain outside portable workflow definitions.

