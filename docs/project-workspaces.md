# Project folders and workspaces

## Current implementation

Studio offers a local project registry and project selection for catalog-backed projects. Those workflows live in a project-scoped SQLite catalog; skills, agent profiles, and graph functions use JSON catalogs under the Studio data directory. This is a local single-operator prototype, not a full portable folder/Git workspace. A separate single-root folder mode edits manifest-indexed workflow source files and creates immutable skill and profile revisions directly, as described below.

The Projects page offers **Download source snapshot** and **Import as new project**. The deterministic `.vawsrc.zip` contains a versioned `vaw.project.json` index with stable IDs and SHA-256 for each ordinary JSON asset. Workflow files use a source-only definition/layout format that accepts disconnected drafts; skills, agent profiles, and graph functions are separate JSON assets. The bounded archive reader rejects unsafe or case-colliding paths, duplicate manifest entries, unlisted/missing files, hash mismatches, and oversized content without extracting files. Import validates all content before creating a new project, then restores definitions/layouts and reusable assets. It does not overwrite or merge into an existing project. Tests verify deterministic export, round trips, and isolated project import.

The Studio has a bounded source-directory reader/writer for the same manifest and asset format. It validates directory content through the archive contract, rejects symbolic links/reparse points and oversized files, writes a `.gitignore` for `.vaw/`, and refuses to overwrite an existing destination. The **Open folder** page opens one explicitly entered absolute path and displays manifest-indexed files in a hierarchical project tree. Selecting a workflow opens it in the graph editor; selecting a skill opens a typed revision form; selecting an agent profile opens a typed profile form. Workflow source and new skill/profile revisions use immutable content-addressed JSON files and an atomically replaced manifest pointer; stale manifest hashes reject a save. Old content-addressed files are retained so concurrent readers of an earlier manifest can finish. Skills and agent profiles are added as new versions; an existing revision is never mutated. Graph functions remain read-only. Folder profile, skill, and graph-function sources are supplied to folder-backed workflow editing, while profile tools and providers remain logical requirements resolved by the Studio/consumer host. The page does not yet remember multiple roots or watch external edits. It does not publish catalog revisions or run workflows. Folder writes are not mirrored into the catalog-backed libraries. Runs remain in the host-level run store.

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

Git is authoritative for source history. `.vaw/` is optional disposable client state and must be ignored by Git and omitted from all exports. Local restore points can help recover or test a prior snapshot, but restoring one writes source files back for Git review. Project files must not contain model endpoints, API keys, MCP access tokens, host grants, or local run evidence. Host-owned run history records immutable definition/layout hashes and resolved dependency identities independently of the active Git branch. Consumers configure the single authoritative `IWorkflowRunStore`; runtime writes and read-only observation use that same store. A second write-only run sink is not planned because it could diverge from replay, pause, approval, and interruption evidence.

Studio keeps its configurable run store in app-local host data outside portable project roots so the Run inspector remains useful across sessions. A temporary directory can be selected for a disposable demo or test, with the understanding that the operating system may remove it. Temporary storage does not provide a retention policy. Checkpoint recovery is separate and opt-in; native checkpoint state and matching event/cursor evidence must use the same checkpoint-capable store.

## Consumer artifact

The current `.vawsrc.zip` is a project source snapshot, not a consumer-installable package. The folder editor treats manifest-indexed files as source for its single-root workflow editing slice, while existing catalog-backed projects still use their local catalog. Importing a ZIP creates a separate catalog-backed project; it does not connect that project to an extracted Git folder.

The later consumer artifact (proposed extension `.vawpkg`) should be built from validated project source. It contains the project manifest, selected workflow files, exact required graph-function/skill/profile snapshots, and a dependency manifest with format versions, content hashes, node/provider/MCP requirements, and unresolved logical binding IDs. It excludes `.vaw/`, Git metadata, endpoints, credentials, grants, and run history. A consumer application installs the artifact into its own deployment/release mechanism and resolves implementations, provider clients, MCP servers, credentials, and authorization under its own host policy. The archive is not a NuGet package and does not install executable assemblies.

Do not claim this as consumer-installable until a clean consumer sample can load it, resolve declared runtime IDs/versions and hashes, supply host-owned services, and execute a workflow without Studio or its authoring catalog. Existing single-workflow JSON export remains available in the meantime.

## Local data and version behavior

The target has no project-local in-app revision history. Users use Git branches, commits, and reverts. Optional `.vaw/` recovery snapshots are content-addressed, local-only conveniences; they can be deleted without losing source. Studio may list snapshots to allow a user to inspect or materialize one for testing. A run must identify the exact immutable artifact/content hash it used, not a mutable folder path, current branch, or sidecar snapshot ID. The host run store owns execution evidence and retention.

Migration from the current SQLite-backed catalog must preserve stable workflow/asset IDs and produce ordinary source files. Existing audit/revision data may be retained in local `.vaw/` history for recovery and old run inspection, but it is not copied into project source or represented as Git history. There must be one clear source-of-truth rule at each migration phase; avoid unsignaled dual writes.

## Roadmap sequence

1. Define and test deterministic project-file schemas, manifest validation, source snapshot build/import, stable IDs, and content hashes. **Done for schema 1.0 and the first workflow/skill source editors.**
2. Extend folder-backed authoring to profiles and graph functions while preserving a single authority rule; define an explicit migration/cutover from the current local catalogs. **Immutable profile source authoring and project-library wiring are implemented; graph-function editing and migration/cutover remain.**
3. Add remembered multiple roots, external file watching, atomic saves, and clear external-change/conflict diagnostics. Agent profiles and graph functions remain read-only, and the folder entry is single-root and does not watch for external Git/editor edits.
4. Add optional ignored `.vaw/` snapshots and restore/test selection, proving that deleting the sidecar does not affect project validity.
5. Build the ZIP consumer artifact and prove clean consumer installation/execution with host-supplied dependencies and policy.

The source editor uses validated manifest-listed paths and requires an explicitly entered local folder path. This local Development-only host is not an authenticated production file service. The current implementation does not prove Git merge behavior, multi-process compare-and-swap against arbitrary external editors, consumer installation, or a complete migration from local catalogs.
