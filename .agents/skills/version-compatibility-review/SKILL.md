---
name: version-compatibility-review
description: Use when changing serialization, schemas, revisions, dependency locks, prefabs, skills, sharing/import, collaboration, Git integration, or release compatibility.
---

# Version and compatibility review

Read [project-guide](../project-guide/SKILL.md) and serialization/versioning sections of [the design](../../../docs/design.md).

## Inspect compatibility surfaces

Identify document schema, stable IDs, node versions/configuration schemas, prefab/skill versions, extension hashes, MCP contract fingerprints, public APIs, and event formats affected. Keep these identities distinct.

- Published definitions/dependency sets are immutable; restoring creates a new revision. Existing runs retain their original definition and resolved artifacts.
- Loading an old definition must report absent/incompatible dependencies. Playback should still work from captured evidence.
- Prefab upgrades are explicit with declared overrides, dependency validation, and cycle checks. Sharing never transfers credentials or grants.
- Imports validate schema, types, references, sizes, and paths. Documents cannot instantiate arbitrary CLR types or cause assembly installation.
- Keep executable JSON separate from layout. Preserve stable IDs and semantic ordering; define canonicalization before relying on hashes.
- Optimistic concurrency must reject stale writes. Test deletion/edit/connect conflicts and ensure undo does not erase other authors' work when collaboration is implemented.

## Validate the change

Use representative old/new fixtures for migrations and round trips when formats exist. Preserve originals; migrations produce drafts. Exercise semantic validation as well as JSON structure. Test deterministic exports without sorting meaningful arrays. Verify environment promotion remaps authorized bindings while retaining the same release identity.

For Git integration, define ownership and conflict handling between repository snapshots and live drafts. Do not claim automatic semantic merges are safe merely because text merges cleanly. Update schemas, examples, migration guidance, and public docs together. Report unsupported or untested historical versions explicitly.

Graph presentation uses separate `WorkflowLayout` schema 1.0; never add coordinates, labels, comments, sections, or reroutes to executable JSON to make UI persistence convenient. `IWorkflowDesignCatalog` saves both documents under one generation and snapshots layout at publication. Test layout-only changes leave execution hashes unchanged and restore/playback use the published layout. Layout sidecar imports validate current node/connection identities and all coordinate/text/route bounds.

Reusable pipelines are copy-on-insert expansion of an exact catalog publication, with fresh IDs and informational schema-1.3 origins. Do not turn origin metadata into executable resolution or authorization. Preserve hashes for absent optional origins in older schemas. Packages carry derived requirements and definition/layout checksums; validate all against contents and preserve meaningful waypoint order. Host registrations and grants never travel with an export.

Schema 1.4 introduces condition configuration and conditional/merge nodes; absent condition properties stay omitted to preserve old hashes. Layout/package/database versions are independent and unchanged. Both new ports carry strings. Guard linear-only editing/reuse against routing graphs; package sharing may preserve a whole conditional graph without claiming graph-prefab expansion.
