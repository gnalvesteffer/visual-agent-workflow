---
name: audit-playback-review
description: Use when changing execution events, administrative audit, payload recording, live reconnect, historical playback, redaction, or retention.
---

# Audit and playback review

Read [project-guide](../project-guide/SKILL.md). Distinguish execution evidence from administrative audit and from runtime checkpoints.

## Check capture and attribution

Capture immutable workflow/dependency identities, node and distinct invocation IDs, prefab scope, attempt/iteration, causal/message correlation, event identity, cursor, and timestamps. Node completion does not prove a particular edge transferred a message: instrument the actual transfer.

Keep successful no-result workflow termination distinct from `run.completed` and from failure/cancellation. Its terminal event should contain no workflow payload, remain terminal for storage/control/recovery, and identify the visible terminator. Do not record adapter-only handoffs as graph connections or make a disconnected output node appear completed during playback.

Attribute definition changes, publication, permissions, controls, approvals, and sensitive-detail access to authenticated actors. Use append-only records and explicit retention/deletion policies; corrections do not rewrite prior facts. Do not describe ordinary append-only tables as tamper-proof.

## Validate replay and reconnection

- Persist enough resolved graph and node metadata to inspect history when original assemblies or providers are absent.
- Recorded playback must make zero executor, model, extension-loading, or MCP calls. Verify with fail-on-call fixtures when implementing playback.
- Check loops, retries, parallel branches, and nested prefab instances remain distinguishable.
- Verify snapshot-plus-stream has no gap and handles duplicate delivery and cursors correctly.
- Scrubbing or changing playback speed changes presentation only; it must not alter stored events.
- Show redacted, truncated, expired, missing, and uncertain content explicitly. Do not infer successful external completion from a request record.

Review payload authorization, redaction before persistence, encryption, size bounds, artifact references, and retention. Test separate actors/workspaces for sensitive run data. Report precisely which behavior was exercised; static event examples alone do not validate the live recording path.

For run control, sequence pause/resume records and executor evidence through one writer. A model/node event arriving after a pause request must not reset `pausing`; only later control or terminal transitions do. Derive playback status from the recorded prefix, display latest status separately, and verify scrubbing leaves stored evidence unchanged. Development actors are placeholders, not authenticated attribution.

For consumer observation use the read-only `IWorkflowRunObserver`, not mutation-capable clients. Snapshot reads and cursor pages must preserve run/revision identity and monotonic sequences. Summary lists contain no payloads. Endpoint mapping is opt-in and requires a host policy; authorization must also govern guessed-ID lookup and any host-specific tenant filtering. Observing/scrubbing/disconnecting must leave journal evidence unchanged. Do not confuse authenticated reads with automatic administrative access auditing.

Approval evidence binds run/revision/content, approval node/invocation, target node/reserved invocation, binding/contract and exact input/hash. Decisions record the host-derived actor and fingerprint; identical retries preserve the original record/actor. A recorded approval is not proof of tool dispatch/completion. Render historical approval evidence without controls. The development actor remains a placeholder; production attribution and payload governance are host responsibilities.

File journals now publish complete `.snapshot.json` records by rename, preferring them over unchanged legacy header/JSONL files after the first successful write. SQLite stores events and sequence/status in one transaction. Verify consistent read-only prefixes, legacy evidence and failure after event insertion; never infer crash-resumable execution from committed evidence.

Checkpoint JSON is stored separately from execution-event payloads with a version/runtime stamp, pinned identities, bounded native state, payload hash and exact cursor. SQLite commits the latest frame plus capture/suspension evidence atomically. Observation/playback consumes snapshots only, not native deserialization or resume. A suspended checkpoint run has no live pause continuation; recovery appends an explicit marker before execution.

Model and MCP evidence now includes `modelCallId`/`toolCallId` per request. Correlate outcomes by kind/node/invocation/call, never clear a missing external outcome merely because its node/run failed. Legacy events without call IDs require one unambiguous outstanding request; ambiguous/unknown histories fail closed. Read-only reconciliation returns metadata at an exact cursor, not payloads/native checkpoint state or a worker-liveness/ownership claim. See [reconciliation](../../../docs/run-reconciliation.md).
