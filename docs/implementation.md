# Implementation status

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

## Remaining roadmap

Live model-backed agents, approvals, arbitrary graph editing, layout/dependency bundle schemas, durable recovery, workspace authorization and full administrative audit, prefab/skill packages, extension isolation, collaboration, and production operations remain outstanding. Later milestones must update this document with verified capabilities and limitations.

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

This is a visual linear pipeline editor, not the full Blueprint-style free-form canvas. There is no drag-to-wire, layout persistence, branching, multi-user live synchronization, or automatic save yet. SQLite catalog persistence does not change the development-only security or file run-journal recovery limitations.

## Milestone 6: node-scoped MCP tools

Added an MCP adapter using ModelContextProtocol 2.3.0, verified against its package XML and source commit `9a327829ef066c44a0d737c58bf3c7617d7c701c`. A trusted host registers logical tool bindings and connection factories. Workflow nodes store only binding requirements and contract fingerprints, never commands, connection URLs, credentials, or permission grants. The designer lists registered bindings and can replace/insert a processing node with an MCP tool.

Schema 1.1 adds `core.mcp-tool` with a single string input/output. Existing schema 1.0 definitions and their hashes remain unchanged because absent optional configuration is omitted from JSON. The tool contract fingerprint covers the tool name and normalized input/output schemas, not its external implementation behavior. Calls rediscover and compare the actual contract before dispatch. Unavailable or drifted bindings fail rather than being silently upgraded.

Invocation scopes are created by the trusted runtime from the immutable node definition. Effective access requires both the node declaration and a host policy grant; guessed aliases and grants belonging to another node do not confer access. Authorization is rechecked before dispatch. Scopes reject use after disposal and enforce at most eight calls, 16,384-character input/output limits, and a default ten-second deadline per operation. No automatic retry is performed. Registrations default to non-read-only and are denied until explicitly designated read-only by the host; MCP annotations alone never authorize calls.

The development host registers a known stdio fixture with pure uppercase/reverse tools and an explicit policy allowing those two aliases. Server commands are fixed by host code, and child processes do not inherit host environment variables or credentials. This is not a server sandbox: the fixture is trusted code. Arbitrary user executables, remote server configuration, authentication, writes, approvals, generalized JSON arguments, resources, sampling, and elicitation are not implemented.

Recorded events include requests, denials, authorization with binding/server/tool/contract and input, outputs, and sanitized failures. Cancellation/transport loss after dispatch is labeled outcome-unknown where observed; it is not represented as confirmed rollback. The local plaintext capture and file-journal limitations remain. Unit/integration checks exercise real stdio discovery/calls, scope isolation, contract mismatch, revocation, input bounds, pre-dispatch connection timeout without retry, non-read-only denial, and old hash compatibility. HTTP/browser checks exercise pipeline execution and saved binding selection. They do not establish production remote-service security or durable side-effect recovery.
