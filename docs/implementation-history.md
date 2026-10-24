# Implementation history

Historical delivery notes and validation evidence. Use the [roadmap](roadmap.md) for current priorities and remaining work, the [architecture guide](architecture.md) for the current implementation, and the [user guide](user-guide.md) for tasks. Earlier milestone limitations describe that point in development; later entries supersede them.

## Milestone 1: core definition and revision contracts

Implemented a .NET 10 solution with a framework-independent core library and xUnit tests. SDK version is pinned in `global.json`; NuGet dependencies use committed lockfiles. CI restores in locked mode, builds, and tests.

Supported definitions use schema `1.0` with stable node/connection IDs, explicit `out` → `in` string data links, and implementation version `1.0.0`. The supported subset is exactly one input, zero or more uppercase/reverse transformations, and one output in a connected linear pipeline. Branches, joins, cycles, unknown node types/versions, and invalid references are rejected. Limits are 256 nodes and 255 connections.

JSON imports reject unknown and duplicate properties, enforce depth and size limits, and perform semantic validation. Serialization normalizes node/connection ordering with ordinal comparison. SHA-256 identifies the v1 serialized definition content; it is not a signature or an authorization mechanism. This initial JSON contract is a subset of the broader illustrative design, not support for the entire planned format.

Immutable revision snapshots carry identity, parent, author, timestamp, definition, and content hash. Restoring creates a new revision with the current revision as its parent and preserves prior objects. These are domain contracts, not yet a persistent revision store or authenticated publication service. The caller must obtain actor identity from a trusted host.

Tests cover round trips, deterministic hashing, operation changes, restore lineage/isolation, invalid endpoints and ports, disconnected cycles, unsupported types/schemas, missing graph data, and ambiguous/unknown JSON fields.

## Milestone 2: Agent Framework execution adapter

The adapter references `Microsoft.Agents.AI.Workflows` 1.26.0 and translates each validated node into an `Executor<string, string>`, connects actual framework edges, and runs through `InProcessExecution.RunStreamingAsync`. No custom scheduler replaces framework execution.

Each run emits platform-owned lifecycle, invocation, input/output, and connection-received events. Transfer evidence is captured inside the receiving executor, rather than inferred from source completion. Sequence numbers are scoped to this single linear run; invocation IDs identify the actual handler calls. Reverse operates on Unicode scalar values, not user-perceived grapheme clusters.

Integration tests exercise framework execution, transformed output, exact delivered payloads, correlation and sequence metadata, pre-cancellation, and rejection of oversized input before execution. The initial input limit is 16,384 characters. The event callback is host-supplied; this adapter alone does not persist history or provide durable scheduling. Events contain plaintext payloads and are suitable only for controlled local development until host redaction/access policies are implemented.

## Milestone 3: local studio, background execution, and recorded viewing

Added a Blazor Razor Class Library, standalone Interactive Server host, and server-owned bounded execution queue. The reusable `WorkflowStudio` accepts an `IWorkflowClient`; `WorkflowRunViewer` reads only a supplied snapshot. See [embedding](embedding.md).

The studio selects one of three deterministic string-pipeline templates, starts execution, lists recorded runs, inspects node payloads, and steps/scrubs through recorded evidence. It is an execution/viewing foundation, not the full Blueprint-style graph editor. Observation polls snapshots; the API also exposes an event cursor for incremental reads.

A single-process file journal retains immutable run definition/revision snapshots and append-only JSONL events with flush-to-disk writes. Reads verify content hash, identity, and event sequence. Completed history survives host restart. Unfinished runs are marked interrupted and never silently replayed. This is not checkpoint recovery or a production durable engine: it lacks multi-process coordination, transactional crash repair, quotas/retention, redaction, encryption, and tamper evidence. A corrupt/torn record fails reading rather than inventing missing evidence. Repeated full reads are appropriate only for small local histories.

The development host accepts only loopback peers/hosts and rejects foreign browser origins (including WebSocket upgrades) and cross-site requests. It refuses non-Development startup. These controls are not authentication; do not expose it through a public reverse proxy. Recorded inputs/outputs are plaintext. No arbitrary extensions or external I/O are enabled. Actor attribution is the placeholder `local-developer`, not an authenticated audit identity.

Verified checks include journal reopening/terminal immutability, sequence/revision mismatch rejection, interrupted-run handling, backend-owned execution, and rendering recorded payloads with no execution services registered. Functional local HTTP checks exercise real framework execution, output, event cursors, invalid definitions, and host/origin denial. Chromium checks exercise actual run controls, output inspection, recorded stepping, reload, mobile overflow, and absence of browser runtime errors. These do not establish a complete accessibility review or production security. The earlier completed HTTP run was also retrieved after restarting the host.

## Milestone 4: transactional workflow catalog

The development host now persists drafts, numbered immutable published revisions, active/head pointers, and catalog audit entries in `.local/workflows.db` using Microsoft.Data.Sqlite 10.0.13. Definition changes use immediate SQLite transactions and require an expected generation. Stale save, publish, restore, and activation requests return HTTP 409 without changes or extra audit entries. Publishing creates a new numbered revision and activates it atomically. Restoring an older revision changes the draft only; publishing that draft creates a new revision whose parent is the current head. Activating an older published revision changes the active pointer without rewriting the draft or publication history.

Published execution resolves the stored revision identity and content. Editing the draft afterward cannot change that run's definition. A convenience endpoint runs the active revision; another runs a specifically selected published revision. Ad-hoc template execution remains available separately.

Catalog mutations append actor, timestamp, kind, generation, revision identity, and content hash to the audit table in the same transaction. The actor is still the development placeholder `local-developer`; this is not authenticated administrative audit, tamper evidence, or run-access auditing. Revision reads validate stored content hashes; hashes do not prove authenticity. The initial database schema is version 1 and unknown schema versions fail explicitly.

Tests and the API smoke check cover persistent history, restore lineage, activation, concurrent writer conflicts across independent store instances, transactionally consistent audit entries, rejected cross-workflow revisions, and execution of the original published content after draft edits. The run journal and scheduling limitations from milestone 3 remain unchanged.

See [workflow API](workflow-api.md) for routes.

## Milestone 5: reusable visual pipeline authoring and version controls

Added `PipelineEditor` and `WorkflowDesigner` Razor components and integrated them into Studio through the public catalog/client interfaces. Users can create a workflow, insert approved transformations, choose operations, reorder/remove nodes, undo/redo edits, save a draft, and publish/activate a numbered version. Rewiring is automatic for the linear model; stable node IDs and unrelated connection IDs are retained. Input/output terminals cannot be removed or moved. The supported 256-node limit also applies to edits.

The designer can preview a published revision read-only, compare structural changes by stable identity, return to its current draft, restore an older revision into the draft, activate a published revision, and run a selected exact version. Unsaved draft changes are distinct from published execution. Local edits block workflow switching/publication until saved or explicitly discarded. A stale save displays a conflict and preserves the local graph rather than silently reloading or overwriting server state.

Definition JSON can be downloaded, exported into a text editor, and imported into the current draft. Imports validate the supported schema/operations, retain the current workflow ID, and remain local until explicitly saved/published. This is definition sharing only, not a complete prefab/dependency bundle or automatic capability binding.

Core tests cover identity preservation, insertion/reorder/removal, operation diffs, terminal and unknown-operation rejection, and the node limit. Real-browser smoke checks cover editing/undo/redo, two publications, recorded output from the selected version, read-only historical preview, restore as a third publication, competing tabs with conflict recovery, JSON import, and mobile overflow. Accessible names and disabled-until-interactive controls are tested through actual controls. The saved-workflow selector is keyed by identity/generation to preserve alignment when its options change after initial save.

At this milestone, authoring was a visual linear pipeline editor. The later graph-canvas milestone below adds free placement and layout persistence; arbitrary drag-to-wire, branching, multi-user live synchronization, and automatic save remain unsupported. SQLite catalog persistence does not change the development-only security or file run-journal recovery limitations.

## Milestone 6: node-scoped MCP tools

Added an MCP adapter using ModelContextProtocol 2.3.0, verified against its package XML and source commit `9a327829ef066c44a0d737c58bf3c7617d7c701c`. A trusted host registers logical tool bindings and connection factories. Workflow nodes store only binding requirements and contract fingerprints, never commands, connection URLs, credentials, or permission grants. The designer lists registered bindings and can replace/insert a processing node with an MCP tool.

Schema 1.1 adds `core.mcp-tool` with a single string input/output. Existing schema 1.0 definitions and their hashes remain unchanged because absent optional configuration is omitted from JSON. The tool contract fingerprint covers the tool name and normalized input/output schemas, not its external implementation behavior. Calls rediscover and compare the actual contract before dispatch. Unavailable or drifted bindings fail rather than being silently upgraded.

Invocation scopes are created by the trusted runtime from the immutable node definition. Effective access requires both the node declaration and a host policy grant; guessed aliases and grants belonging to another node do not confer access. Authorization is rechecked before dispatch. Scopes reject use after disposal and enforce at most eight calls, 16,384-character input/output limits, and a default ten-second deadline per operation. No automatic retry is performed. Registrations default to non-read-only and are denied until explicitly designated read-only by the host; MCP annotations alone never authorize calls.

The development host registers a known stdio fixture with pure uppercase/reverse tools and an explicit policy allowing those two aliases. Server commands are fixed by host code, and child processes do not inherit host environment variables or credentials. This is not a server sandbox: the fixture is trusted code. Arbitrary user executables, remote server configuration, authentication, writes, approvals, generalized JSON arguments, resources, sampling, and elicitation are not implemented.

Recorded events include requests, denials, authorization with binding/server/tool/contract and input, outputs, and sanitized failures. Cancellation/transport loss after dispatch is labeled outcome-unknown where observed; it is not represented as confirmed rollback. The local plaintext capture and file-journal limitations remain. Unit/integration checks exercise real stdio discovery/calls, scope isolation, contract mismatch, revocation, input bounds, pre-dispatch connection timeout without retry, non-read-only denial, and old hash compatibility. HTTP/browser checks exercise pipeline execution and saved binding selection. They do not establish production remote-service security or durable side-effect recovery.

## Milestone 7: Agent Framework agents with scoped tools and model evidence

Schema 1.2 adds `core.agent` with a pinned host provider ID/version, bounded instructions, and up to eight unique tool requirements. Optional absent configuration remains omitted so earlier definition hashes remain compatible. Pure and MCP string-processing nodes can be mixed with agent nodes in the same linear workflow. Agent node creation, instructions, tool selection, immutable publication, and reload are available through the public Razor editor.

The executor uses Agent Framework 1.26.0 `ChatClientAgent` backed by a host-created `IChatClient`. A function-invoking client limits execution to eight model rounds, serial tool invocation, and one consecutive tool error; tool functions capture only the node-scoped broker context. Missing grants fail preflight before provider creation. Unknown provider versions fail rather than selecting a substitute. The existing broker enforces declarations, policy, contract identity, and call budgets on actual invocation, including after discovery.

The development host registers only `demo.scripted` version 1.0.0. This is a deterministic test provider: it requests the first assigned tool once and returns its result, or echoes input without tools. It does not interpret instructions, reason, or call a live model. This verifies the actual framework agent/function loop without credentials; it is not evidence of LLM quality or live-provider integration. Host applications can register their own owned `IChatClient` factories; no external provider credentials were present or tested in this environment.

Agent events record provider/model identity, tool aliases/requirements, lifecycle, and bounded model request/response evidence. Text, function-call arguments, and function results are projected into the record. Unsupported content is explicitly marked; token streaming, multimodal payload capture, and provider-side hidden reasoning are not implemented. Requests/outputs are plaintext in the local journal under the same development restrictions as earlier milestones. Agent execution has a thirty-second cancellation deadline, a requested 1,024-output-token budget, a 16,384-character final-text limit, and a 262,144-character per-model-trace capture limit. A requested token budget is not proof that a provider enforces it.

Provider faults are recorded without exception details. Active cancellation is tested through the workflow: framework streams may end normally on cancellation, so the adapter checks the execution token before declaring missing output. Failed/cancelled node events prevent terminated nodes from remaining labeled Running in live/history views. No automatic retry or durable provider resumption is introduced.

Integration tests cover the real agent-to-MCP round trip, model evidence, no-tool server isolation, denied requirements before model creation, provider version mismatch, sanitized provider failure, active cancellation, guessed function names, configuration validation, schema preservation, and semantic round trips. HTTP/browser smoke checks cover agent publication, instructions and node tool assignment, recorded model rounds, and persisted reload. Live external-provider behavior remains unrun. See [runtime configuration](runtime-configuration.md).

## Graph canvas, routing, and annotations

Added a shared Razor canvas with edge-mounted string sockets, freely positioned nodes, browser-local drag previews, pan/zoom, SVG wires, reroute points, pinned curve tangents, and orthogonal routes. Numeric fields and keyboard nudges complement pointer gestures. Nodes support user labels/comments. Named section frames track explicit membership and move their nodes together. Shared undo/redo includes presentation and semantic edits.

Presentation is a separate, bounded `WorkflowLayout` artifact. SQLite schema 2 migrates existing catalog data and atomically saves layout plus definition under one workflow generation. Published revisions capture immutable layouts; preview, restore, reload, and recorded playback retain them. Layout-only edits leave execution definitions and hashes unchanged. Separate JSON sidecar import/export preserves existing definition contracts. Legacy host catalogs remain compatible with presentation edits disabled.

The runtime still supports only validated linear string pipelines. Freely positioning cards does not add branching, arbitrary rewiring, collaborative editing, or durable scheduling. See [graph editor research, controls, contracts, and verification](graph-editor.md).

## Core reuse and portable package contracts

Added extraction of consecutive processing steps into a workflow, copy-on-insert expansion of exact published versions with fresh identities, preserved layout/annotations/routes, and schema-1.3 origin metadata. Existing 1.0–1.2 serialization remains compatible. Portable package format 1.0 includes definition/layout hashes and exact derived requirements, with duplicate/unknown property and size validation. Expanded nodes use the existing runtime and per-node capability checks; no dynamic source execution or grant inheritance is added. Core and real framework execution tests cover the contracts. Studio authoring controls follow in the next milestone.

## Studio reusable pipelines and package sharing

Added reusable section extraction into a separate draft, exact published-version selection and insertion, source/version labels, registration-availability review, and package export/import in the shared designer. Existing catalog lifecycle/CAS applies; insertion and package import are one undoable local edit. Sources and consumers remain independent. Package exports include a consistent saved design or immutable published layout; imports preserve current workflow identity and require explicit save/publication.

Real-browser validation covers selecting an older source while a newer version exists, fresh identities, expected framework output, undo/redo, annotation/layout package round trips, mismatch rejection, extraction without publishing or overwriting the consumer, stable workflow selection, and denial of an imported missing MCP binding. Existing core/runtime/canvas/embedding checks remain. This is copy-on-insert reuse, not nested runtime prefabs, auto-upgrades, source authentication, or permission transfer.

## Cooperative pause/resume and QA guidance

Added optional Core run-control interfaces and a framework execution gate, backend-owned bounded continuations, shared execution permits, and sequenced pause/resume evidence. Studio and embeddable components expose paused starts and pause/resume controls without requiring existing clients to implement the new capability. Exact publications remain pinned and recorded playback shows historical control state separately from the latest status.

Added Anthropic's pinned Apache-licensed webapp-testing skill and repository-specific QA guidance. Applying browser verification caught a graph accessible-name regression; explicit names now separate ID, type and state. Pause/resume verification covers in-flight boundaries, idempotent/withdrawn commands, cancellation, admission limits, other-run progress, owner loss, UI reload, revision identity and playback. The Release build and all 93 .NET tests passed. See [run control](run-control.md) for guarantees and limits. Process restart interrupts unfinished work; durable recovery and production authorization remain planned.

Final browser regression passed all five scripts: `studio_smoke.py --browser`, `ui_skill_review.py`, `graph_canvas_smoke.py`, `pipeline_reuse_smoke.py`, and `pause_resume_smoke.py`, each using system Chromium. The mobile overflow check caught a malformed stylesheet edit during development; restoring existing theme styles and limiting the change to run-control rules fixed it. Local links and all 11 Mermaid diagrams passed local parsing/rendering. The new pause browser regression is included in CI.

## Standalone service consumer proof

Added `samples/WorkflowService`, a Generic Host consumer without Studio/Blazor references, HTTP listeners or an instantiated catalog. Reviewed package fixtures are mapped to exact operator-owned revision/hash pins. Direct and paused-admission queued modes execute real Agent Framework pipelines; a host-owned policy gates the real local MCP reverse fixture. Missing providers/bindings, denied grants, contract drift, invalid packages and incorrect pins fail before run creation. An existing registration cannot be borrowed by another workflow.

The standalone smoke check passed with Studio stopped. It verified pure/MCP outputs, direct/queued identity and events, pre-admission failures and SIGTERM cancellation of a parked child. The sample uses project references and existing Hosting infrastructure; ASP.NET Core/SQLite remain transitive dependencies. It is not a durable production worker or published NuGet SDK.

## Shared local hosting registration

Added `AddLocalWorkflowRuntime` for .NET hosts, with lazy journal creation, one runtime/worker and client/control aliases, preserved host overrides, idempotent same-path registration and rejection of conflicting paths. Studio and the standalone sample use it. Catalogs, publication-aware clients, providers, tool grants and application triggers remain explicit host configuration. Current source consumption and future package-release prerequisites are documented separately from production guarantees.

The new immediate Generic Host stop test reproduced a .NET 10 scheduling race: stopping before the background loop started bypassed its cleanup and left an admitted paused run nonterminal. Stop now finalizes pending ownership once the background task is complete, even if its loop never ran; it does not mark runs terminal underneath a still-active writer after a stop timeout. The regression is retained with registration/lifetime checks. No serialized schema or existing constructor/signature changed.

Final validation passed: Release build with zero warnings/errors, all 98 .NET tests, the standalone direct/queued/MCP/shutdown smoke check, Studio API and browser journeys, and the pause/resume browser suite. Local links and all 12 Mermaid diagrams parsed and rendered locally. No live LLM, production recovery, public package publication or multi-user security claim is made.

## Opt-in consumer observation and embedded graph

Added a Core read-only observer contract with payload-free summaries, snapshots and sequenced cursor pages. Hosting maps protected GET-only routes only when a consumer explicitly opts in with its own named authorization policy. The local journal reader is independent of executors/providers, and a separate Observation.Http project depends only on Core. The client checks identities, hashes, sequences and an 8 MiB response limit. No mutation endpoint or workflow schema migration is introduced.

Added `WorkflowObservationPanel` for consumer embedding and Studio's consumer-source page. Studio holds named endpoint credentials on the server, restricts destinations to HTTPS or loopback HTTP, disables redirects/cookies, and never accepts arbitrary browser-supplied URLs. Source replacement/disposal cancels observation waits and rejects late responses. Recorded playback and disconnect leave consumer evidence unchanged; an outage preserves explicitly marked last received evidence and reconnect refreshes it. The panel renders the captured semantic graph at default positions; saved layout sidecars are not transported yet.

The standalone sample's new `serve` mode leaves observation disabled by default and demonstrates explicit single-operator bearer authentication when enabled. Studio port/data-root configuration permits isolated test instances; concurrent processes never share the smoke test's journal. Production tenant filtering, per-user delegation, payload governance, observation-access audit storage and scalable event stores remain host responsibilities or planned work.

Validation passed: locked restore, zero-warning Release build, all 110 .NET tests, isolated two-process observation smoke, standalone direct/queued/MCP/shutdown smoke, existing Studio API/browser journeys, UI/theme/embedding checks and pause/resume regression. All 13 Mermaid diagrams parsed/rendered locally and documentation links passed. Tests verify disabled endpoints, 401/403 policy denial, payload-free summaries, cursor pages/live status changes, no mutation routes, embedding without runtime services, source replacement, cancellation of observation waits, bounded/corrupt HTTP responses, server-only consumer access and restart/reconnect. Observation smoke is included in CI. No live LLM or production durability/security qualification is claimed.
