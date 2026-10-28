# Decision 0009: Live MCP pin descriptions stay display-only

**Status:** Accepted, bounded implementation

## Context

Direct MCP nodes expose one typed data pin per supported top-level tool
argument. The tool schema also carries optional descriptions and JSON Schema
defaults. A Blueprint-like editor should make argument intent discoverable, but
server-provided schema annotations are untrusted and can drift independently of
the workflow definition.

## Decision

The Studio derives a top-level MCP argument description at render time only
when the live binding matches the node's pinned contract and description hash.
The description is exposed on the transient `WorkflowPortDescriptor` and in the
selected node's Inputs view. It is bounded, safely rendered text and is never
serialized into the workflow, included in a package, used as an instruction, or
sent to an MCP server.

JSON Schema `default` remains annotation-only. It is not copied into a node,
does not satisfy a required pin, and never changes invocation behavior. An
author who wants a static value must use the existing MCP arguments editor;
those values remain separate, explicit workflow configuration.

## Compatibility and safety

No schema version, persisted field, content hash, runtime argument resolver, or
authority contract changes. Existing definitions and package hashes therefore
remain unchanged. If the binding is unavailable, stale, malformed, or has an
overlong/control-character description, the pin remains usable and simply has
no description. Persisted MCP pin identity, type, requiredness, and ordering
continue to win over live discovery.

Persisting or applying schema defaults is deferred until a separate decision
defines authored-default provenance, canonicalization, secret handling,
migration, approval/evidence behavior, and live-schema compatibility.

## Verification

Core coverage proves that descriptions are transient descriptor metadata and do
not mutate persisted MCP pin definitions. The Studio's existing graph editor
path supplies the matching binding and renders the description only in the
selected node's input-pin inspector.
