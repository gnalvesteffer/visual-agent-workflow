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
