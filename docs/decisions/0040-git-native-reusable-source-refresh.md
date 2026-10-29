# Git-native reusable source refresh

**Status: First bounded slice shipped; mutable editing and rebinding remain open**
**Decision date: 2026-10-29**

Folder-backed projects keep two identities deliberately separate:

- the Git-facing source file, identified by a stable `(kind, id, version)` path;
- the immutable dependency snapshot embedded in a workflow, identified by its
  content hash.

The first slice adds deterministic stable paths for skills, agent profiles, and
pure graph functions and refreshes the manifest hash when a valid stable source
file is edited outside Studio. Content-addressed legacy/revision files are not
rewritten or replaced. A source edit therefore cannot silently change an
existing workflow or historical run.

On project read, stable reusable files are parsed and validated before their
manifest hashes are refreshed. Invalid JSON, identity drift, unsupported asset
format, unsafe paths, and semantic validation failures remain fail-closed.
Filesystem watcher events remain hints; the source-directory read is the
authoritative reconciliation point.

This slice does not yet make the Studio save form mutate an existing stable
asset, rebind embedded workflow snapshots, or persist a complete dependency
closure in run evidence. Those are separate gates requiring explicit stale
reference diagnostics, an update/rebind action, bounded transitive closure
rules, and immutable run provenance. Existing catalog-backed projects and
content-addressed folder revisions retain their current behavior.

The required proponent/opponent/adjudicator review recommended this revised
sequence. The opponent identified the reproducibility risk of live mutable
dependency resolution; the proponent identified the smallest stable-path and
hash-refresh slice; the adjudicator recommended proceeding with this source
refresh before mutable saves, dependency rebinding, migration, or run-closure
changes.
