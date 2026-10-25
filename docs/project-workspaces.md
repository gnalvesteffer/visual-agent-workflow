# Project folders and workspaces

## Current implementation

Studio currently offers a local project registry and project selection. Workflows live in a project-scoped SQLite catalog; skills, agent profiles, and graph functions use JSON catalogs. The project registry and catalog roots are under the Studio data directory. This is a local single-operator prototype, not a portable folder/Git project format.

The first access to the deterministic Default Project copies a legacy workflow database with SQLite backup and copies legacy JSON libraries, preserving the source files. Existing tests verify migrated workflow definitions, revision IDs and hashes, layouts, generations, head/active pointers, audit order, and skills. Model-provider and MCP registrations, endpoints, protected credentials, grants, and run history are host-wide. Project selection does not authorize runtime capabilities.

## Target project contract

The portable project directory is the canonical editable source. It must be openable and understandable without Studio, SQLite, or `.vaw/`. Studio will open several folders in a workspace and show the real project files in a tree; selecting a workflow opens it in the graph editor. Git runs through the user's normal tooling. Studio will not provide multi-user editing or an in-app Git client.

The proposed source tree is:

```text
my-project/
  .gitignore                 # excludes .vaw/
  vaw.project.json           # format version, stable ID, name, relative indexes
  workflows/
    support-triage.json      # definition, layout, parameters, exact asset snapshots/references
  skills/
    research/1.0.0.json
  agents/
    support-specialist/1.2.0.json
  functions/
    normalize-ticket/1.0.0.json
  .vaw/                      # optional local-only restore points, test snapshots and indexes
```

Paths in manifests are relative and must remain under the project root after canonicalization. Reject path traversal, unsupported links that leave the root, duplicate IDs, case-colliding paths, oversized files, and unknown format versions with actionable diagnostics. Save one source file atomically. When an external Git/editor change conflicts with an open unsaved editor buffer, preserve both versions and ask the operator to reload or compare; never overwrite the external edit silently.

Git is authoritative for source history. `.vaw/` is optional disposable client state and must be ignored by Git and omitted from all exports. Local restore points can help recover or test a prior snapshot, but restoring one writes source files back for Git review. Project files must not contain model endpoints, API keys, MCP access tokens, host grants, or local run evidence. Host-owned run history records immutable definition/layout hashes and resolved dependency identities independently of the active Git branch.

## Consumer artifact

Studio should build a ZIP artifact from validated project source (proposed extension `.vawpkg`). The archive contains the project manifest, selected workflow files, exact required graph-function/skill/profile snapshots, and a dependency manifest with format versions, content hashes, node/provider/MCP requirements, and unresolved logical binding IDs. It excludes `.vaw/`, Git metadata, endpoints, credentials, grants, and run history. A consumer application installs the artifact into its own deployment/release mechanism and resolves implementations, provider clients, MCP servers, credentials, and authorization under its own host policy. The archive is not a NuGet package and does not install executable assemblies.

Do not claim this as consumer-installable until a clean consumer sample can load it, resolve declared runtime IDs/versions and hashes, supply host-owned services, and execute a workflow without Studio or its authoring catalog. Existing single-workflow JSON export remains available in the meantime.

## Local data and version behavior

The target has no project-local in-app revision history. Users use Git branches, commits, and reverts. Optional `.vaw/` recovery snapshots are content-addressed, local-only conveniences; they can be deleted without losing source. Studio may list snapshots to allow a user to inspect or materialize one for testing. A run must identify the exact immutable artifact/content hash it used, not a mutable folder path, current branch, or sidecar snapshot ID. The host run store owns execution evidence and retention.

Migration from the current SQLite-backed catalog must preserve stable workflow/asset IDs and produce ordinary source files. Existing audit/revision data may be retained in local `.vaw/` history for recovery and old run inspection, but it is not copied into project source or represented as Git history. There must be one clear source-of-truth rule at each migration phase; avoid unsignaled dual writes.

## Roadmap sequence

1. Define and test deterministic project-file schemas, manifest validation, package build/round-trip, stable IDs, and content hashes.
2. Convert the project catalogs to folder-backed source with a single authority rule and safe migration from the current local catalogs.
3. Add a workspace that opens multiple project directories, an expandable file tree, file watching, atomic saves, and clear external-change/conflict diagnostics.
4. Add optional ignored `.vaw/` snapshots and restore/test selection, proving that deleting the sidecar does not affect project validity.
5. Build the ZIP consumer artifact and prove clean consumer installation/execution with host-supplied dependencies and policy.

The current registry/catalog tests do not prove Git merge behavior, portable project interchange, safe arbitrary-folder access, or consumer installation.
