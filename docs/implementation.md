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

MCP brokering, real agents, approvals, arbitrary graph editing, persistent workflow draft/publication/history services, layout/dependency bundle schemas, durable recovery, workspace authorization and administrative audit, prefab/skill packages, extension isolation, collaboration, and production operations remain outstanding after milestone 3. Later milestones must update this document with verified capabilities and limitations.
