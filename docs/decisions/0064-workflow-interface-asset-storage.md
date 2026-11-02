# Decision 0064: Workflow interface asset storage

**Status:** Accepted; project revision storage and source archive support implemented (2026-11-02). Studio asset editing and interface-call binding remain deferred.

## Decision

Workflow interface contracts and implementation revisions are project-owned
assets. Each is immutable and identified by its asset ID, positive revision
version, revision ID, and semantic content hash. Catalog-backed project
workspaces persist them in `workflow-interfaces.json`; source snapshots export
them as manifest-indexed `.interface` and `.implementation` files. Folder
projects use the same two manifest kinds and content-addressed immutable paths.

The project manifest advances to format 1.3 for these new asset kinds. Formats
1.0, 1.1, and 1.2 remain readable. A current Studio write upgrades the manifest
to 1.3. Existing workflow definition bytes and hashes are not changed by this
project-format migration. Workflow schema 2.0 remains the serialized call
contract and embeds its exact implementation revision and selected operation.

Project assets are an authoring and reuse source. Runtime execution never looks
up an interface or implementation from a mutable project catalog. When an
author selects an implementation revision, Studio must copy the exact revision
and operation into the workflow call before save, package capture, or run
admission. Editing, deleting, or moving an asset cannot silently change an
existing call. Missing source assets are project diagnostics; embedded call
snapshots remain self-contained and executable under normal host admission.

These files carry no endpoint, credential, grant, approval, or authorization.
Content hashes establish identity, not authority. Implementation snapshots
embed exact child workflow definitions, even though this duplicates content in
call nodes and packages; deduplication may reduce transport size but must not
replace the pinned execution authority.

## Current implementation boundary

- `StudioWorkflowInterfaceLibrary` persists immutable revisions for catalog
  projects and participates in source archive export/import.
- `StudioFolderWorkflowInterfaceSource` lists and writes immutable revisions
  under the folder project's optimistic manifest hash.
- Manifest validation checks extensions, asset IDs, versions, revision
  integrity, and matching manifest identities.
- Folder Explorer currently displays these files as read-only. There is not yet
  a typed editor, operation-to-workflow binding UI, dependency audit, or
  interface-call selection control in the graph editor.

## Compatibility

An invalid manifest, duplicate asset identity, mismatched revision hash, or
missing indexed file is rejected. Import validates the complete source snapshot
before creating a project. No existing workflow is rewritten. A future repair
or migration of an unresolved interface call must preserve the original
definition and create a new draft; it must never guess an operation or refresh
a pinned revision.

## Follow-up

1. Add interface and implementation editors with conformance diagnostics.
2. Bind implementation operations to exact workflow snapshots and support
   explicit revision creation.
3. Add the graph-editor picker that embeds an exact implementation revision
   and operation in the call node, retaining stable field IDs and typed pins.
4. Audit project dependencies, source archive round trips, package closure,
   host admission, and passive playback after project assets are unavailable.

See [Decision 0063](0063-workflow-interface-contract-foundation.md),
[project folders and workspaces](../project-workspaces.md), and the
[Blueprint-inspired roadmap](../roadmap.md).
