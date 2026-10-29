# Decision 0042: record reusable dependency identity with folder runs

## Status

Accepted and implemented as a bounded increment toward immutable run closure.

## Context

Folder-backed runs already capture the complete executable workflow definition and
layout, plus project, workflow, manifest, and layout hashes. They did not
explicitly enumerate the reusable skill, profile, graph-function, or nested
workflow identities embedded in that definition. A later source edit must not
change what an admitted run means, and playback must not consult the current
folder to reconstruct it.

## Decision

`WorkflowRunDependencyClosure` derives a sorted, deduplicated manifest from the
embedded definition. Each entry records only its dependency kind, stable ID,
version, and semantic content hash. The closure also records a deterministic
SHA-256 hash over its canonical entries and is bounded to 256 entries and 256
KiB of serialized identity data.

Folder and restore-point captures attach this closure to
`WorkflowRunProvenance`. Run-store validation checks the closure's bounds,
internal hash, and exact derivation from the captured definition. The field is
optional for backwards compatibility, so historical catalog and folder runs
without it remain readable.

## Boundaries

The run header already contains the exact embedded executable definition, so
this slice does not duplicate prompt or dependency source bytes. It also does
not record MCP endpoints, credentials, grants, or provider authority. Playback
never re-resolves current project files. Exact source-byte closure artifacts,
resolver/runtime facts, retention policy, and catalog-run parity remain open
work in the immutable run-closure roadmap item.
