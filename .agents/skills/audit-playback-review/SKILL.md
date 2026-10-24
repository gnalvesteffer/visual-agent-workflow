---
name: audit-playback-review
description: Use when changing execution events, administrative audit, payload recording, live reconnect, historical playback, redaction, or retention.
---

# Audit and playback review

Read [project-guide](../project-guide/SKILL.md). Distinguish execution evidence from administrative audit and from runtime checkpoints.

## Check capture and attribution

Capture immutable workflow/dependency identities, node and distinct invocation IDs, prefab scope, attempt/iteration, causal/message correlation, event identity, cursor, and timestamps. Node completion does not prove a particular edge transferred a message: instrument the actual transfer.

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
