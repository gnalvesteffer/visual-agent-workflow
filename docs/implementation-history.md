# Implementation history

Delivered milestones are listed in reverse chronological order. This page records completed work and its scope; for the current product boundary and priorities, see the [roadmap](roadmap.md). Detailed behavior and limits remain in the linked feature documentation.

## Persistent Studio theme selector

The Studio navigation now offers Dark Modern, Light Modern, VS Code Dark+, Solarized Dark, Nord, Monokai, and High Contrast themes. A small synchronous bootstrap applies the browser's saved choice before the stylesheet paints; the interactive selector updates and persists it. A Blazor enhanced-navigation callback restores the root theme after route transitions, and each selector reads the saved preference. Theme tokens adapt the standalone shell and graph together, while embedded components continue to inherit their host's own theme.

## Flat Studio theme and fitted graph viewport

The standalone Studio now uses a flat, light neutral shell with a single dark graph canvas. Its subdued grid fills the whole viewport and tracks camera pan and zoom rather than ending at the logical world's edge. Initial attachment and **Fit graph** center visible nodes using transient camera offsets; workflow layout coordinates remain unchanged. Browser review covers full-width grid, initial centering, fit, graph gestures, theme contrast, forced colors, reduced motion, and narrow embeds.

## Windows history compatibility and read-only browser smoke

Portable LF hashes now verify historical Windows CRLF hashes for run records, published revisions and shared packages without rewriting their evidence. New definitions and layouts use portable line endings. A Linux browser smoke assertion now checks that insertion controls are absent in read-only revision view without opening an authoring-only disclosure. Tests cover legacy hash rehydration, file-journal listing and package import; the real Chromium Studio smoke passes locally.

## Cross-platform definition hashes and file-journal replacement

Windows CI exposed portability defects: indented JSON used CRLF when hashing workflow definitions, and file replacement could fail while another in-process store instance read the snapshot. Serialization now normalizes line endings to LF. File journal instances for one path now share an in-process access gate, with bounded retries for transient OS file-access denials. The local stdio MCP fixture pins an initialize-capable protocol version so cold startup cannot cause a false fallback from the newer discover probe. The .NET matrix, including Windows, passes.

## schema-1.6 workflow parameters

Workflow definitions can carry up to 64 non-secret string parameters with stable IDs, display names and defaults. The Studio can manage them, add explicit workflow-value getter nodes, and supply per-run overrides for a published revision. Existing run client/store contracts remain supported through optional parameter capabilities; built-in file/SQLite headers capture the fully resolved map for playback. Reusable pipeline expansion copies used parameter definitions and remaps conflicts. Core, Agent Framework and Hosting tests cover validation, legacy JSON omission, defaults, overrides, execution, persistence and replay; a headless browser smoke test covered author, save, publish, run and playback. Types, required parameters, exposed agent configuration pins and mutable variables remain future work. Current controls and boundaries are in [workflow parameters](workflow-parameters.md).

## Provider management and Studio theme refinement

The Settings page now lists, creates, edits as a new immutable version, and deletes locally saved OpenAI-compatible provider configurations. Credentials remain protected in local Studio storage, descriptors remain secret-free, and executor registrations update immediately. The settings and standalone Studio shell use a more restrained graphite and steel-blue theme with improved hierarchy and responsive forms. Host configuration remains available for consumer services. The Studio remains a loopback-only unauthenticated prototype; multi-user credential management and workflow-reference checks before deletion are not implemented. See [provider configuration](model-providers.md) and [design system](design-system.md).

## canvas-first creation workbench

Blank connected drafts, searchable node library, contextual search/actions, drag placement and output-pin creation use validated insertion paths. Compact cards and a selected properties panel prioritize the canvas. Designer/run views preserve state; native fullscreen includes editing/playback controls, with fit, keyboard shortcuts, filtering and clearer recorded paths. Browser validation covers these gestures and existing authoring/version/run flows. Arbitrary existing-node rewiring, explicit branch-block removal and broader graph semantics remain planned. See [graph editor](graph-editor.md) and [user guide](user-guide.md).

## read-only interrupted-run reconciliation

Cursor-bound assessments explain unsettled node/model/tool/approval facts, terminal status and saved/missing/invalid/advanced checkpoint state. New per-call model/tool IDs distinguish repeated calls, with conservative legacy matching. Built-in observers, the protected GET API, bounded HTTP client and embeddable observation panel expose metadata without native state, payloads or mutation controls. Eligibility is suppressed on cursor races and source failures and does not grant ownership. See [run reconciliation](run-reconciliation.md).

## deterministic saved-suspension recovery

An opt-in consumer host saves native Agent Framework JSON checkpoints atomically with audit/state and resumes an exact deterministic suspension after host replacement. Tests and a forced-process-loss proof cover stable revision/identity, both branch paths, no duplicate nodes, ownership exclusion, stale evidence and rollback. Built-in ordinary runtime hosts also enforce exclusive local ownership. SQLite run schema 2 migrates existing evidence. Studio's queue retains interruption semantics; external-node/approval and arbitrary-crash recovery remain excluded. See [checkpoint recovery](checkpoint-recovery.md).

## run storage boundary

The local runtime/controller now consume a host-selected `IWorkflowRunStore`. Atomic bounded batches and expected-sequence checks keep evidence and derived status consistent. File snapshots preserve legacy reads; an optional dedicated SQLite store supplies transactions and independent-connection concurrency checks. Studio/service configuration, observer consistency and rollback/conformance checks are implemented. Runtime ownership remains exclusive; restart still interrupts unfinished work. See [run storage](run-storage.md).

## opt-in host-configured model adapter

Studio and the standalone service can explicitly enable version-pinned OpenAI Chat Completions factories. The optional library owns fresh bounded SDK clients, server-side secrets, cancellation/deadlines, safe categorized failures and no automatic retries. Agent evidence adds response IDs and token usage. Unit and local protocol/browser/service fixtures verify the SDK path without live model calls. Credentialed deployment qualification remains planned; see [model providers](model-providers.md).

## exact-action approvals and deterministic vertical slice

Schema 1.5 adds a backend-owned approval immediately before an explicit MCP tool. Requests bind the pinned revision, exact input/binding/contract and reserved invocation identity; decisions are sequenced and host-attributed. Waiting releases execution ownership, while pause and approval remain independent. Designer/review/playback controls and the scripted Agent → Condition → Approval → MCP → Output browser proof are implemented. Remote observation remains read-only. Restart interrupts unfinished waits; durable approval recovery and live-model validation remain planned. See [approvals](approvals.md).

## conditional string graphs

Schema 1.4 adds approved ordinal predicates, true/false string ports, exclusive merges and bounded acyclic validation. The adapter uses real Agent Framework conditional edges and records decisions separately from actual deliveries. Designer controls insert conditional blocks, edit predicates/arms and preserve undo/version/package/playback behavior. Existing linear schemas/hashes remain supported; linear-only reuse rejects routing graphs. See [conditional graphs](conditional-graphs.md). Richer port types, arbitrary socket rewiring, loops and parallel joins remain planned.

## opt-in consumer observation

Consumers can explicitly map policy-protected read-only summary/snapshot/cursor endpoints. Studio attaches to operator-configured remote sources through a separate HTTP observer; the same `WorkflowObservationPanel` embeds in consumer Blazor apps without execution services. API, lifecycle and two-process browser checks cover read-only access, playback, source replacement and reconnect. The endpoint is disabled by default in the sample. See [consumer observation](observation.md) for authentication, current default-layout/polling limits and host-owned audit requirements.

## standalone service integration and local hosting API

The [standalone consumer](../samples/WorkflowService/README.md) runs reviewed pinned packages directly or through a backend-owned queue without Studio/Blazor or an authoring catalog. It verifies real MCP, independent node grants, missing/drifted dependency rejection and host-stop cancellation. The sample and Studio share the additive [local hosting DI helper](hosting-sdk.md). Project-reference consumption is documented; stable NuGet distribution and minimal worker dependency packaging remain planned. Conditional string graph authoring is now implemented; the deterministic approval/MCP slice is implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

## cooperative pause/resume

Process-owned runs can start paused, pause between nodes, resume without reexecuting completed nodes, and cancel while paused. API and optional embedded controls share sequenced audit events; recorded playback includes pause state. Paused contexts release the execution permit so other admitted work can run. The 32-context budget includes paused runs. The ordinary queue interrupts unfinished continuations; the separate deterministic checkpoint host now supports saved-suspension recovery. See [run control](run-control.md).

## reusable pipelines and sharing

Core contracts, Studio controls, and end-to-end validation are complete. The headless service-integration proof and local registration API are complete. Conditional string graph authoring is now implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

| Deliverable | Status | Completion evidence |
| --- | --- | --- |
| Extract consecutive processing steps into a separate workflow | Complete | Core extraction preserves annotations and rejects invalid selections |
| Insert an exact published source version with fresh identities | Complete | Copied requirements/source identity, bounded expansion, real framework execution without source lookup |
| Portable definition/layout packages with hashes and requirement manifests | Complete | Canonical round trips, bounds, mismatch/unknown/duplicate-property rejection |
| Save a labeled section as a reusable draft and choose published versions | Complete | Real controls preserve the consumer draft and source history |
| Insert a selected version with shared undo/redo and review requirements | Complete | Browser checks, saved consumer output, reload and immutable source-version behavior |
| Export/import the full package through Studio | Complete | Layout/annotation preservation, local-only import, explicit save/publication, no permission transfer |

This increment uses copy-on-insert template expansion, not recursive runtime prefab calls or automatic instance upgrades. See [reusable pipeline contracts](reusable-pipelines.md).
