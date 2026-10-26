# Roadmap

The goal is a dependable, extensible C# platform for visually designing agent workflows, hosting them as logic inside user-owned services, running them independently of the browser, and inspecting exact historical execution. Studio is an optional IDE; the execution engine must be independently consumable. This document tracks delivery priorities and status. [Architecture](architecture.md) describes what exists, [the user guide](user-guide.md) explains how to use it, and [implementation history](implementation-history.md) retains detailed milestone evidence. [Product design](design.md) records the broader direction.

Statuses mean **Complete** (implemented and verified), **Active** (currently being built), **Next** (next planned increment), or **Planned** (direction with unresolved details). Planned work is not a supported capability. Sequence expresses dependencies rather than dates.

## Current product boundary

The development Studio edits and runs validated linear, conditional, bounded parallel and typed agent graphs through Microsoft Agent Framework. Workflows have optional human-readable names while their generated IDs remain stable for storage, imports, workflow calls, and consumer integrations. Most graph connections carry strings; schema 1.9 adds a bounded JSON entry input and pointer selector that returns text, schema 1.13 adds bounded JSON projection and JSON workflow output, schema 1.14 adds one visible two-branch fork/barrier join returning bounded JSON, schema 1.15 adds declared JSON value kinds to projection fields, schema 1.16 adds recursive object and array-item mapping, schema 1.17 adds one auditable bounded loop with a linear body and a hard iteration cap, schema 1.18 adds no-result termination exits, schema 1.19 adds bounded calls to exact child workflow revisions, schema 1.20 permits up to four sequential bounded parallel blocks and success/failure outcomes for termination nodes, schema 1.21 adds per-agent model-turn/time controls and optional structured JSON results, schema 1.22 adds an optional provider-reported aggregate token budget across the agent and its delegates, schema 1.23 separates execution and data wires, schema 1.24 adds typed Boolean branching, schema 1.25 adds Boolean constants and NOT, schema 1.26 adds bounded nested/reconvergent Boolean routes and multiple exits, schema 1.27 adds embedded pure graph-function snapshots as lazy typed data nodes, schema 1.28 lets agents invoke only exact assigned pure graph-function snapshots as bounded tools, schema 1.29 lets Agent nodes bind bounded output-token and total-token controls to integer workflow parameters, schema 1.30 lets an Agent choose among authored execution pins through an invocation-scoped internal function, schema 1.31 adds typed signed-64-bit integer values and checked arithmetic, and schema 1.32 adds one explicit static fork/join region with 2–8 named branches. The Studio can build this region from a valid schema 1.31 graph, select its initial branch count, edit labels while preserving IDs, and remove/undo the region; populated branch resizing and reordering remain planned. The Studio has free node placement, compatible pin rewiring, wire disconnect, saved incomplete drafts with validation gates, edge sockets, multi-selection/group movement, a centered 100% initial view plus pan/reset/fit, reroutes/pinned angles, labels/comments, named sections, immutable publications, restore/activation, persisted local audit/run evidence, and recorded playback, plus reusable published workflows and portable packages. Conditional arms can take separate routes to return through the workflow Output or end at a success/failure termination node; merges are optional when branches need shared downstream work. Agent nodes configure system instructions and a stable model connection ID, node-scoped MCP and graph-function tools, per-response and aggregate token budgets, model turns that accept any positive limit or `-1` for unlimited, optional timeouts and structured results, and provider-advertised reasoning controls including explicit off. During execution, agent nodes show provider-reported token totals, latest completed prompt usage versus the configured context window (marked as the last completed measurement while another model request is in flight), recorded host context compactions, and an estimated cost when both optional token rates are configured. The current adapter does not configure host-side compaction; provider-internal compactions cannot be observed. Workflows do not pin connection revisions; editing a connection changes future runs, while recorded events retain the model descriptor and usage used for replay. Studio has a local Streamable HTTP and stdio MCP library with a bounded nested argument subset and a text-only versioned skill library; selected skill revisions and structured user-message templates travel in schema 1.7 definitions. Stdio launches trusted local programs with structured arguments, explicit encrypted environment values, disabled environment inheritance, OS-user privileges, and no sandbox; a fresh process starts for discovery and each invocation. Consumer hosts can register digest-pinned container MCP tools through the node-scoped broker and may load one SHA-256-pinned .NET extension inside that isolated worker; these are host-code integrations, not Studio connection forms. Schema 1.6 supports up to 64 typed scalar parameters; schema 1.8 adds integer, number, boolean and choice validation and exposes a reasoning-effort input. Schema 1.11 adds versioned agent profiles and bounded one-level subagent delegation; schema 1.12 adds profile-pinned coordination channels with audited run-scoped exchange. A parent agent can select up to four sequential delegate calls and share messages among assigned profiles; its parallel team tool uses a separate per-team channel scope. Nested parallelism and nested loops remain planned. Model connections can be managed in Studio settings by stable ID. The default agent connection is a deterministic fixture; the opt-in OpenAI SDK adapter is protocol-verified, while credentialed live-service qualification remains outstanding.

The backend owns execution. Core schema 1.32 and Studio support one explicit 2–8 branch fork/join region, with branch-count selection at creation and stable named pins; resizing/reordering a populated region is not available yet. Studio/ordinary queued runs still interrupt after host failure; a separate opt-in host explicitly resumes deterministic saved checkpoints. The Studio host is loopback-only and unauthenticated; payloads are plaintext. It is not production ready. Socket rewiring is supported for matching typed pins, while incomplete drafts remain non-executable until validation succeeds. Sequential subagent calls, parallel agent-team calls, and bounded graph fan-outs are explicit; shared-channel graph coordination remains planned. Conditional routing and parallel graph execution are explicit validated operations.

## Completed milestone: portable selected-workflow releases

The folder editor builds deterministic `.vawpkg` archives from explicit saved workflow selections. Core validates bounded archive structure, hashes, inner workflow packages, and the derived dependency union without extracting files. The standalone WorkflowService can select and execute an entry from the bundle with host-supplied services and policy, independently of Studio/catalog state. A clean-host smoke verifies pure execution and MCP denial until an explicit consumer grant is supplied. The release is not an installer or signed publisher artifact; immutable staging/activation and catalog-backed project export remain future work. See [project releases](project-workspaces.md#consumer-artifact), [service integration](service-integration.md), and [sample commands](../samples/WorkflowService/README.md).

## Completed milestone: bounded agent outcomes and resource budgets

Schema 1.21 adds a configurable model-response limit (positive integer, default 8; `-1` means unlimited), an optional positive whole-number timeout that overrides the model connection default (blank uses the connection default), and structured results. Schema 1.22 adds an optional 1–100,000 provider-reported total-token budget shared across the agent and delegated profiles. The adapter reduces later output allowances and stops before tool execution when a response reports usage above budget; missing usage fails closed. Concurrent requests already in flight and an oversized prompt can exceed the reported-token threshold, so this is not a guaranteed billing cap. The Linux CI model test verifies forced `submit_answer` tool invocation and provider usage under this budget; real-model qualification for structured `submit_result` outcomes remains open. Run-node cost estimates use provider-reported usage and optional configured pricing; hard cost limits need stronger provider guarantees and remain future work. See [agent result and budget design](agent-results-and-budgets.md).

## Completed increment: agent telemetry and timeout precedence

Agent-node timeouts now take priority over model-connection defaults; leaving the node timeout blank uses the connection setting. The node reports the effective timeout in run evidence. Live graph cards show cumulative provider-reported tokens, the last completed prompt's context-window usage while another request is in flight, configured cost estimates, and counts of recorded host context compactions. The adapter does not enable host-side compaction, and provider-internal compactions are not observable. Regression tests verify a 300-second node timeout over a 120-second connection default, stale in-flight context labeling, context-window-unconfigured behavior, and recorded compaction counts. See [model provider runtime evidence](model-providers.md).

## Completed stage: bounded Boolean branch topologies

Schema 1.26 lifts the one-Branch proof restriction while preserving the explicit execution/data split. It allows 1–8 Boolean Branch nodes and up to 16 success/failure termination nodes; Branch outputs target another Branch, Output, or Terminate. Output, Branch, and Terminate can receive multiple alternative routes, enabling reconvergence without a required Merge. The graph must be acyclic, every executable node statically reachable, and at least one structural route to Output present. Predicate feasibility is data-dependent and not statically guaranteed. A reached shared target executes once. The Studio offers a guarded upgrade from valid schema 1.25 drafts and preserves unconnected-node editing. Adversarial review narrowed the design to this bounded DAG and requires tests for one-time reconvergence. See [implementation history](implementation-history.md#bounded-boolean-branch-topologies), [graph values](graph-values.md), [graph editing](graph-editor.md), and [the user guide](user-guide.md#try-the-typed-graph-preview).

## Completed stage: explicit execution and data semantics

The updated [Blueprint capability reference](unreal-blueprint-reference.md) makes explicit execution and data semantics a prerequisite for the visual-programming toolbox. Legacy graph connections keep their original behavior. Schema 1.23 separates execution edges from data dependencies; schema 1.24 adds typed Boolean equality and a two-output Branch; schema 1.25 adds a configurable Boolean constant and Boolean NOT; schema 1.26 allows bounded nested/reconvergent Boolean routes. Playback uses recorded values, connection identities, route decisions, and activations; draft validation permits incomplete topology while publish/run validation blocks it. Schema 1.27 establishes the pure graph-function runtime contract. Preserve legacy serialization and hashes. See [implementation history](implementation-history.md#typed-boolean-primitives), [visual programming and function design](graph-values.md), [graph editing](graph-editor.md), and the [Blueprint capability reference](unreal-blueprint-reference.md).

## Completed milestone: pure graph-function runtime contract

Schema 1.27 provides embedded, immutable pure-function snapshots with named string/Boolean interfaces and data-only bodies. The runtime, hash validation, package requirements, scoped audit payload, and deterministic evaluation are complete. Calls evaluate lazily and consumer hosts need no Studio function catalog. See [visual programming and function design](graph-values.md).

## Completed milestone: Studio function library and visual editor

Studio now has a local, folder-organized function library with immutable hash-pinned revisions and a graph editor for named typed boundaries and the supported pure string/Boolean node set. Authors can save and copy revisions, move/delete library entries, validate bodies, and add an exact selected snapshot to an upgraded workflow. Calls embed snapshots so package consumers need no external catalog. The library remains local to Studio; standalone function package import/export, collaborative function drafts, and agent-tool exposure are not included. See the [user guide](user-guide.md#create-and-reuse-pure-graph-functions), [architecture](architecture.md), and [implementation history](implementation-history.md#studio-pure-graph-function-library-and-editor).

## Completed milestone: graph-defined tools for agents

Schema 1.28 allows an Agent node to embed up to eight exact immutable pure-function revisions and expose only those selections as model tools. Each invocation validates its declared input contract, evaluates the pure function with bounded calls, node visits, and payload sizes, and records correlated start/completion/failure evidence including the pinned content hash and values. Agent MCP permissions are not inherited by graph functions; functions remain deterministic, data-only, and cannot perform external effects. Workflow packages list graph-function-tool requirements and include the exact snapshots. See [graph-defined function tools](graph-values.md#graph-defined-function-tools) and the [user guide](user-guide.md#expose-pure-graph-functions-as-agent-tools).

## Completed milestone: local project-scoped authoring catalogs

The Studio now supports local project creation, selection, archival/restoration, project-scoped workflow catalogs, skills, agent profiles, and graph functions. Opening the deterministic Default Project migrates legacy workflow DB data through SQLite backup and copies legacy JSON libraries, retaining the sources. Project-aware API routes address the selected project; older unscoped routes remain mapped to Default Project. Regression tests cover migration of workflow IDs, revisions/hashes, layout, generation, active pointers, audit, and skill assets, plus project isolation. See [local project workspaces](project-workspaces.md).

This is local organization for one operator, not a multi-user authorization or simultaneous-editing feature. Model-provider and MCP settings, credentials, broker registrations/grants, and run history remain host-wide. Projects do not yet hold environment binding manifests, so project membership does not change tool permission. This catalog-backed milestone is a transition and is not a portable Git project format. See [current persistence](project-workspaces.md) and the [architecture](architecture.md).

## Completed milestone: multi-folder project workspace

Studio can keep up to eight project folders open in tabs. Each folder has its own mounted editor session and tree, so switching tabs preserves unsaved buffers in the current browser circuit. Closing a dirty tab requires explicit discard confirmation. Tab order and selected project ID persist in Studio-local `open-workspace.json`; only the active project watches for filesystem changes, and inactive projects revalidate on activation. Refresh/restart restores the tab list from disk but not in-memory unsaved edits. No portable workspace descriptor, collaboration, or in-app Git was added. See [project folders and workspaces](project-workspaces.md), [architecture](architecture.md), and [implementation history](implementation-history.md#multi-folder-project-workspace).

## Completed milestone: consistent Studio navigation and responsive page layouts

All Studio pages now render the same full-width navigation with project selection, theme selection, and an active-page marker. Content reflows at phone widths; the graph remains a contained scroll surface, and the function editor adapts its library, canvas, and properties panel to narrower embedded hosts. Browser checks visit every Studio route on desktop and mobile and verify that normal and 200% text do not create page-level horizontal scrolling. This improves consistency and accessibility without changing page-specific capabilities.

## Completed milestone: folder-backed Git projects and workspace explorer

The source format exports deterministic `.vawsrc.zip` snapshots with a versioned manifest, stable asset IDs, per-file SHA-256, and separate workflow definition/layout JSON that preserves disconnected drafts. Graph-function assets use a versioned Studio authoring envelope that preserves their canvas layout while keeping the executable function hash limited to its definition. Validated snapshots import as new isolated Studio projects. The bounded directory reader/writer applies the same manifest/hash rules, rejects link traversal and oversized content, and keeps `.vaw/` ignored. **Open folder** reads manifest-indexed project files and displays a hierarchical tree. Selecting a workflow opens its graph editor; selecting a skill or agent profile opens a typed editor; selecting a graph function opens its interface and pure dataflow editor. Workflow drafts and skill/profile/function revisions are written to content-addressed files through a shared per-root lock and an atomically replaced manifest pointer, with stale manifest edits rejected. Existing graph-function revisions remain immutable; edits start from a snapshot and save to a new version. Opened roots are registered in the project selector and reopen through their source editor. Folder-project catalog APIs fail closed with HTTP 409, and the project list downloads a validated snapshot from the selected folder. Folder edits do not write catalog revisions or run evidence into the source project. Existing catalog-backed projects remain SQLite/JSON and are not mirrored from folder mode. Multi-root tabs, external change watching, and host-owned saved/snapshot workflow execution are implemented; see [folder-backed execution](#completed-milestone-folder-backed-project-execution-and-snapshot-testing).

Remaining project work includes immutable consumer staging and activation, trusted publisher verification, and catalog-backed project export. Folder project migrations run offline with a shared single-host lock, staged snapshot validation, a durable mapping journal, and startup recovery; they migrate current drafts/assets only, leaving catalog history and runs in host data. Mutable project-wide variables remain out of scope; workflow parameters remain workflow-owned. Git supplies source control, so there is no planned in-app Git client or multi-user editing. See [folder projects and Git source control](design.md#folder-projects-and-git-based-source-control) and the [migration sequence](project-workspaces.md#catalog-to-folder-cutover-contract).

## Previous completed milestone: Provider management and Studio theme refinement

The Settings page can list, add, edit in place, and delete locally saved OpenAI-compatible model connections by stable ID. API credentials are protected in local Studio storage, descriptors remain secret-free, and edited connection settings apply to future agent runs. The Studio theme and provider-management layout were refined. Host configuration remains available for consumer services. The Studio remains a loopback-only unauthenticated prototype; multi-user credential management is not implemented. See [provider configuration](model-providers.md) and [design system](design-system.md).

## Previous completed milestone: canvas-first creation workbench

Blank connected drafts, searchable node library, contextual search/actions, drag placement, output-pin creation, compatible pin rewiring, and wire disconnection are available. Drafts can be saved while incomplete; visible validation issues block publish and execution. Compact cards and a selected properties panel prioritize the canvas. The canvas supports box selection, grouped movement, pan space around the graph, fit/reset controls, keyboard shortcuts, filtering and clearer recorded paths. Browser validation covers these gestures and existing authoring/version/run flows. Complete branch-block removal is undoable. Broader graph semantics remain planned. See [graph editor](graph-editor.md) and [user guide](user-guide.md).

## Previous completed milestone: versioned agent profiles and bounded delegation

The Agent Designer creates and organizes immutable specialist revisions. Workflow agent nodes can expose selected profile versions as delegate tools; each profile pins its provider, instructions, optional inference settings, MCP bindings, and skill snapshots. The runtime limits each parent to four profiles and four calls, permits one delegation level, applies the profile tool list and host grants, and records child invocation evidence linked to the parent. Package import/export preserves profile dependencies without transferring endpoints, credentials, or grants. Deterministic integration tests cover profile selection, child tool isolation, audit correlation, package requirements, and reusable-workflow insertion. See [agent profiles](agent-profiles.md).

## Previous completed milestone: read-only interrupted-run reconciliation

Cursor-bound assessments explain unsettled node/model/tool/approval facts, terminal status and saved/missing/invalid/advanced checkpoint state. New per-call model/tool IDs distinguish repeated calls, with conservative legacy matching. Built-in observers, the protected GET API, bounded HTTP client and embeddable observation panel expose metadata without native state, payloads or mutation controls. Eligibility is suppressed on cursor races and source failures and does not grant ownership. See [run reconciliation](run-reconciliation.md).

## Previous completed milestone: deterministic saved-suspension recovery

An opt-in consumer host saves native Agent Framework JSON checkpoints atomically with audit/state and resumes an exact deterministic suspension after host replacement. Tests and a forced-process-loss proof cover stable revision/identity, both branch paths, no duplicate nodes, ownership exclusion, stale evidence and rollback. Built-in ordinary runtime hosts also enforce exclusive local ownership. SQLite run schema 2 migrates existing evidence. Studio's queue retains interruption semantics; external-node/approval and arbitrary-crash recovery remain excluded. See [checkpoint recovery](checkpoint-recovery.md).

## Previous completed milestone: run storage boundary

The local runtime/controller now consume a host-selected `IWorkflowRunStore`. Atomic bounded batches and expected-sequence checks keep evidence and derived status consistent. File snapshots preserve legacy reads; an optional dedicated SQLite store supplies transactions and independent-connection concurrency checks. Studio/service configuration, observer consistency and rollback/conformance checks are implemented. Runtime ownership remains exclusive; restart still interrupts unfinished work. See [run storage](run-storage.md).

## Previous completed milestone: opt-in host-configured model adapter

Studio and the standalone service can explicitly enable version-pinned OpenAI Chat Completions factories. The optional library owns fresh bounded SDK clients, server-side secrets, cancellation/deadlines, safe categorized failures and no automatic retries. Agent evidence adds response IDs and token usage. Unit and local protocol/browser/service fixtures verify the SDK path without live model calls. Credentialed deployment qualification remains planned; see [model providers](model-providers.md).

## Previous completed milestone: exact-action approvals and deterministic vertical slice

Schema 1.5 adds a backend-owned approval immediately before an explicit MCP tool. Requests bind the pinned revision, exact input/binding/contract and reserved invocation identity; decisions are sequenced and host-attributed. Waiting releases execution ownership, while pause and approval remain independent. Designer/review/playback controls and the scripted Agent → Condition → Approval → MCP → Output browser proof are implemented. Remote observation remains read-only. Restart interrupts unfinished waits; durable approval recovery and live-model validation remain planned. See [approvals](approvals.md).

## Previous completed milestone: conditional string graphs

Schema 1.4 adds approved ordinal predicates, true/false string ports, exclusive merges and bounded acyclic validation. The adapter uses real Agent Framework conditional edges and records decisions separately from actual deliveries. Designer controls insert conditional blocks, edit predicates/arms and preserve undo/version/package/playback behavior. Branches can route independently to the workflow Output or a successful no-result termination exit; merges remain available for shared downstream work. Existing linear schemas/hashes remain supported; linear-only reuse rejects routing graphs. See [conditional graphs](conditional-graphs.md). Richer port types and nested or variable parallel blocks remain planned; bounded loops are supported only under the constraints in [bounded loop graphs](loop-graphs.md).

## Previous completed milestone: opt-in consumer observation

Consumers can explicitly map policy-protected read-only summary/snapshot/cursor endpoints. Studio attaches to operator-configured remote sources through a separate HTTP observer; the same `WorkflowObservationPanel` embeds in consumer Blazor apps without execution services. API, lifecycle and two-process browser checks cover read-only access, playback, source replacement and reconnect. The endpoint is disabled by default in the sample. See [consumer observation](observation.md) for authentication, current default-layout/polling limits and host-owned audit requirements.

## Previous completed milestone: standalone service integration and local hosting API

The [standalone consumer](../samples/WorkflowService/README.md) runs reviewed pinned packages directly or through a backend-owned queue without Studio/Blazor or an authoring catalog. It verifies real MCP, independent node grants, missing/drifted dependency rejection and host-stop cancellation. The sample and Studio share the additive [local hosting DI helper](hosting-sdk.md). Project-reference consumption is documented; stable NuGet distribution and minimal worker dependency packaging remain planned. Conditional string graph authoring is now implemented; the deterministic approval/MCP slice is implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

## Previous completed milestone: cooperative pause/resume

Process-owned runs can start paused, pause between nodes, resume without reexecuting completed nodes, and cancel while paused. API and optional embedded controls share sequenced audit events; recorded playback includes pause state. Paused contexts release the execution permit so other admitted work can run. The 32-context budget includes paused runs. The ordinary queue interrupts unfinished continuations; the separate deterministic checkpoint host now supports saved-suspension recovery. See [run control](run-control.md).

## Previous completed milestone: reusable workflows and sharing

Core contracts, Studio controls, and end-to-end validation are complete. The headless service-integration proof and local registration API are complete. Conditional string graph authoring is now implemented; the opt-in host-owned SDK adapter is protocol-verified; credentialed live qualification remains planned.

| Deliverable | Status | Completion evidence |
| --- | --- | --- |
| Extract consecutive processing steps into a separate workflow | Complete | Core extraction preserves annotations and rejects invalid selections |
| Insert an exact published source version with fresh identities | Complete | Copied requirements/source identity, bounded expansion, real framework execution without source lookup |
| Portable definition/layout packages with hashes and requirement manifests | Complete | Canonical round trips, bounds, mismatch/unknown/duplicate-property rejection |
| Save a labeled section as a reusable draft and choose published versions | Complete | Real controls preserve the consumer draft and source history |
| Insert a selected version with shared undo/redo and review requirements | Complete | Browser checks, saved consumer output, reload and immutable source-version behavior |
| Export/import the full package through Studio | Complete | Layout/annotation preservation, local-only import, explicit save/publication, no permission transfer |

This increment uses copy-on-insert template expansion, not recursive runtime prefab calls or automatic instance upgrades. See [reusable workflow contracts](reusable-workflows.md).

## Previous bounded increment: conditional block removal

The editor can remove an isolated condition, both arms, nested decisions and the matching merge after listing the affected nodes for confirmation. It can also remove a merge to split branch routes toward separate work or the common Output. Core rejects shared incoming paths and reconnects the surrounding graph without changing published revisions. Unit and browser evidence is recorded in [implementation history](implementation-history.md).

## Previous completed milestone: independent condition routes

Conditional arms can now end at the workflow Output or take separate downstream paths. Removing an optional merge leaves a rewritable draft; Output accepts up to 16 alternative routes and returns whichever value reaches it. A merge is still available when branches need to rejoin for shared work. Runs continue to require a string or bounded JSON output; no-result termination remains a separate future capability.

## Delivery sequence

```mermaid
flowchart TD
    Reuse["Complete: reusable workflows and packages"] --> Hosting["Complete: service consumer and local hosting API"]
    Hosting --> Observe["Complete: opt-in consumer observation"]
    Observe --> Graph["Complete: conditional string graph authoring"]
    Graph --> Vertical["Complete: deterministic agent, branch, approval and MCP slice"]
    Vertical --> Models["Complete: opt-in model adapter with protocol verification"]
    Models --> Store["Complete: atomic run storage boundary"]
    Store --> Recovery["Complete: explicit deterministic checkpoint recovery"]
    Recovery --> Assess["Complete: read-only run reconciliation"]
    Assess --> Agents["Complete: configurable MCP servers, tools, and agent skills"]
    Agents --> JsonData["Complete: bounded JSON input, selection, and projection"]
    JsonData --> Controls["Complete: per-agent inference controls"]
    Controls --> Delegation["Complete: versioned agent profiles and bounded subagent delegation"]
    Delegation --> Teams["Complete: bounded parallel agent teams"]
    Teams --> Channels["Complete: run-scoped agent channels"]
    Channels --> DelegationRounds["Complete: parent-coordinated sequential subagent turns"]
    DelegationRounds --> JsonProjection["Complete: bounded JSON projection"]
    JsonProjection --> Orchestration["Complete: bounded parallel graph block"]
    Orchestration --> ProjectionTypes["Complete: declared JSON projection value kinds"]
    ProjectionTypes --> TypedMapping["Complete: recursive object and collection mapping"]
    TypedMapping --> Loops["Complete: bounded loop"]
    Loops --> Exit["Complete: explicit workflow return and termination"] --> Calls["Complete: typed pinned calls, parameter mapping, and revision upgrades"]
Calls --> Assets["Complete: child graph drill-in"] --> NestedApproval["Complete: scoped nested approvals"] --> SequentialParallel["Complete: sequential parallel blocks"]
    Assets --> ContainerWorkers["Active: container workers, brokered capabilities, and .NET extensions"] --> Production["Planned: production qualification"]
    Orchestration --> Durable["Later: host-attributed reconciliation decisions"]
    Durable --> Collaboration["Workspace sharing and collaboration"]
    Collaboration --> Production
```

## Previous completed milestone: explicit workflow return and termination

Schema 1.18 keeps Output as the normal workflow result and adds `core.terminate` for successful exits without a result. Conditional paths can independently return a value or terminate; no merge is required unless the paths need shared work. The run store and playback distinguish `run.terminated` from `run.completed`, and the runner exposes a typed outcome while preserving its existing string-returning methods. Termination is bounded to 16 string exits per workflow, does not roll back earlier external effects, and is not yet eligible for checkpoint resume. See [workflow termination](workflow-termination.md).

## Previous completed milestone: bounded JSON projection

Schema 1.13 adds a `Project JSON fields` node that creates a new object from 1–16 named JSON Pointer paths and supports a JSON-typed workflow output. Studio edits mappings in the node properties and offers the node on JSON paths. Validation enforces typed pins and bounded field names/configuration; runtime caps JSON depth and payload sizes and records the exact projected value for playback. Existing agents and MCP nodes remain string-based, so use the JSON selector to convert a chosen value to text before connecting to them. Core schema/package/editing tests and an Agent Framework run test passed. Full .NET suite: 275 passed, one opt-in live-model test skipped. Documentation links and all 24 Mermaid diagrams passed. See [architecture](architecture.md), [user guide](user-guide.md#select-and-project-json-input), and [implementation history](implementation-history.md#bounded-json-projection-and-typed-workflow-output).

## Previous completed milestone: parent-coordinated sequential subagent turns

The parent model now coordinates specialists by choosing when to call each assigned `delegate_N` tool. Sequential delegate calls share bounded coordination channels across child invocations, scoped to that parent agent invocation. The parallel team tool remains fixed fan-out and receives a separate channel board per team call. Profiles pin up to eight channel memberships; each message is limited to 4,000 characters, each channel to 32 messages, and each board to 64 messages. Reads and writes, including the exact messages returned, are recorded under child invocation evidence for replay. Delegation remains one level deep and capped at four child executions per parent invocation; child tools still require both the profile assignment and host authorization. The deterministic regression test verifies manager-selected order, cross-turn message visibility and audit scope. See [agent profiles](agent-profiles.md), [architecture](architecture.md), and [the user guide](user-guide.md#design-reusable-subagents).

### Complete: shallow JSON field contracts

Schema 1.15 lets each JSON projection field declare a scalar or container kind, and the runtime checks the selected value before it emits the projected object. This milestone provided the type foundation for recursive mappings in schema 1.16.

### Complete: recursive schema-backed object and collection mapping

Schema 1.16 adds recursive object/array contracts to projection fields. Users map object fields with relative JSON Pointers, mark optional properties, and define the schema applied to each array item. Runtime output contains only explicitly mapped properties; required path/type errors stop the node with safe playback evidence. Mapping depth, node count, configuration size, field count, array length, and payload size are bounded. Existing schema 1.13–1.15 documents retain pass-through behavior. The Blazor editor, serializer/validator tests, and runtime success/error/evidence tests are implemented. Bounded loops and richer control flow remain separate milestones because they change scheduling, cancellation, and failure semantics.

### Complete: one bounded workflow loop

Schema 1.17 supports one loop node on a string graph, with one linear body, a safe continue-while predicate, and a validated maximum of 1–16 iterations. The runtime uses Agent Framework feedback edges, distinguishes the initial and return messages by source node and pin, and records iteration starts/completions and the predicate/cap stop reason for playback. Studio can insert/configure the loop, add body nodes, and remove it while preserving its body as a one-pass route. Conditions, approvals, parallel blocks, JSON paths, nested loops, and early termination are excluded in this increment. Core and Agent Framework regression tests verify edit/validation/removal, predicate exit, maximum enforcement, and recorded evidence. See [bounded loop graphs](loop-graphs.md).

### Complete: explicit workflow return and termination

Schema 1.18 adds the visible `core.terminate` node for successful exits with no result. Condition paths may finish independently without a merge; normal return paths still finish at the workflow Output. Run history distinguishes `run.terminated` from `run.completed`, the typed runner outcome distinguishes no result from an empty string, and playback shows the terminal route without executing it. Termination is string-only, bounded to 16 exits, and does not roll back prior external work. It is not part of the checkpoint-resume subset. See [workflow termination](workflow-termination.md).

### Complete: typed nested workflow calls and revision upgrades

Schema 1.19 call nodes embed the exact child definition, revision/version, and content hash. Child execution stays in the parent run’s ordered audit stream under scoped event paths, and playback does not re-execute dependencies. Call pins inherit the child Input and Output types (`string` or `json`); parent parameters can map into compatible child parameters while unmapped child values use their defaults. The Studio lists dependencies from the pinned snapshot and compares another published revision across child nodes, connections, parameters, skills, profiles, and package requirements. Applying an accepted upgrade changes only the parent draft, preserves compatible mappings, reports removed mappings, and adds one undo step. The old snapshot remains active until the parent draft is saved and published. Validation limits nesting to three levels and 1,024 expanded nodes, rejects call-path cycles and invalid hashes, and requires host authorization for child capabilities. The child graph view is read-only; scoped nested approvals bind to the exact root call path and action while preserving host authorization boundaries. Core contract/editing and Agent Framework execution tests cover the changes. See [nested workflow calls](nested-workflows.md) and [approvals](approvals.md).

### Complete: child graph drill-in

The Studio can inspect the exact child definition embedded in a selected call and inspect a candidate graph during revision review without leaving the parent design. It uses the stored child layout when the catalog provides one and otherwise generates a temporary read-only layout. Inspection does not edit, execute, or authorize the child. Core execution remains bound to the embedded definition and scoped replay events.

### Complete: scoped nested approvals

Approval actions in pinned child workflows bind to the root run/revision/hash, every exact call-node and child-revision/hash edge, the executing definition, and the immediate MCP action/input/invocation. The request event carries the identical child path scope; stale snapshot, node, action, or scope mismatches are rejected. Child approval details render in the approval panel and highlight the parent call in playback. Host grants remain separate and are checked at dispatch. Validation prevents two concurrent approval-bearing branches because the current run controller admits one pending decision. Nested approvals remain process-owned; restart interrupts them. Focused core, runtime, and rendering tests pass. See [approvals](approvals.md) and [nested workflow calls](nested-workflows.md).

### Complete: sequential bounded parallel blocks

Schema 1.20 supports up to four sequential two-branch fork/barrier blocks. Studio can insert another block on a string path after the preceding join and selector, and remove any selected block independently. Earlier schema versions remain limited to one block. Core rejects nested or independently concurrent blocks and bounds total block count; each join retains its own bounded JSON output, progress evidence, and playback path. Core tests cover insertion/removal, the block cap, schema compatibility, and unrelated condition-arm forks; the Agent Framework test runs two joins in order. Branch-local conditions, nested forks, dynamic branch counts, and loops inside parallel branches remain unsupported. See [parallel graph behavior](parallel-graphs.md).

### Active: container MCP workers, brokered capabilities, and .NET extensions

Consumer hosts can register digest-pinned MCP servers in disposable Docker workers using the normal broker and node grants. The worker is launched with no network, inherited environment, or writable root, plus non-root identity, capability dropping, resource bounds, and ephemeral temporary storage. When explicitly configured, it receives one read-only mount containing only a private Unix socket through which it can request up to eight node-assigned MCP tools. Every request is rechecked against the current pinned workflow requirement and host grant; delegation is limited to one level and audit remains host-owned. A worker image can include one approved, versioned .NET extension implementing `IWorkflowExtension`; the host pins its SHA-256, the worker verifies the bytes and declared identity before loading it, and its typed MCP operations execute only inside the container. A Linux CI proof builds the fixture image and checks extension identity, typed invocation, delegated invocation, environment/filesystem/network containment, and node authorization. The local environment builds the pinned Debian-based MCR image; a 256-open-file cap allows MCP schema discovery and ordinary plus extension-tool invocation (128 failed during schema discovery). The managed local host denies binding the brokered Unix-domain socket with `SocketException: Permission denied`, so the nested-capability and remaining isolation assertions have not completed here. The full Linux image-based proof is still required and this milestone remains active. Publisher signatures, a Studio assembly catalog, multi-assembly image packaging, Windows workers, pooling, and durable retries remain unsupported. See [containerized MCP workers](extension-workers.md) and [isolated .NET extensions](extension-assemblies.md).

## Recently completed milestone: typed signed-integer graph values

Schema 1.31 adds integer constants, checked signed-64-bit addition, integer equality, and typed string/Boolean/integer workflow-value getters and pure graph-function pins. The Studio exposes the same subset in workflow and function editors. Overflow deterministically fails at the arithmetic node and is recorded in the run audit. Older schemas retain their existing string behavior. Automated Core and Agent Framework tests cover typing, parameter binding, arithmetic, branch selection, audit evidence, and overflow; a Playwright smoke covers the editor, saving, run evidence, and inspector. See [graph values](graph-values.md), [architecture](architecture.md), and [implementation history](implementation-history.md#typed-signed-integer-graph-values).

## Previous completed milestone: bounded parallel graph block

Schema 1.14 adds one explicit two-branch fork with named branch anchors and a fan-in barrier join. Studio creates, rewires, previews, removes, and undoes the complete block; the join emits bounded JSON and can reconnect to existing text work through a selector. Microsoft Agent Framework 1.26.0 concurrent execution and `AddFanInBarrierEdge` are verified with synchronized deterministic agents. Playback records actual branch transfers, join progress, and the final result without making calls, and the graph card shows which branch result the join is waiting for. Branch failures do not produce a joined value, and external effects are not rolled back. Core/adapter tests and a browser proof passed; the complete .NET suite passed 283 tests with one opt-in live-model test skipped. Nested forks, branch-local conditions, variable branch counts, loops, and distributed transactions remain unsupported. See [parallel graph behavior](parallel-graphs.md), [user guide](user-guide.md#run-two-paths-in-parallel), and [implementation history](implementation-history.md#bounded-parallel-graph-block).

### Complete: service consumer and local hosting registration

The standalone proof loads pinned artifacts, rejects invalid/missing dependencies, enforces node-scoped host grants, records deterministic and real MCP evidence, and demonstrates cancellation/host lifetime without Studio. The shared DI helper preserves custom registrations and adds no mandatory catalog. See [service integration](service-integration.md) and [local hosting API](hosting-sdk.md).

Supported NuGet distribution remains planned: decide package metadata/licensing, public compatibility baselines, minimal dependency separation and independently restored package-consumer tests before release. Current consumers use project references at a pinned source commit. This packaging work does not change runtime guarantees or provide production durability.

### Complete: opt-in host-configured provider adapter

The SDK adapter and configuration flow are implemented with deterministic protocol fixtures retained for CI. See [model providers](model-providers.md). Credentialed live-service qualification is **Planned**, not complete: no authorized credentials were configured. Approval of model-selected tool calls inside agent loops remains a distinct future capability.

### Complete: local run storage boundary

Host-selected storage, atomic event/state batches, concurrency checks, file compatibility and a transactional SQLite implementation are complete. They establish committed evidence, not recoverable execution. See [run storage](run-storage.md).

### Complete: deterministic checkpoint and recovery contract

Native superstep state, a versioned/bounded recovery envelope, atomic checkpoint/audit commits, exact-cursor admission and local owner exclusion are implemented for explicit deterministic saved suspensions. Ordinary queued runs, arbitrary active-process loss and external effects retain their previous limits. See [checkpoint recovery](checkpoint-recovery.md).

### Complete: read-only interrupted-run reconciliation contract

The assessment distinguishes a saved suspension, advanced checkpoint evidence, unresolved work, invalid/missing frames and terminal status. Consumer and embedded observation paths expose the explanation without making live calls or decisions. See [run reconciliation](run-reconciliation.md).

### In progress: advanced agent authoring and workflow inputs

Schema 1.6 adds workflow-owned typed parameters with stable IDs, labels, defaults, optional bounded descriptions, visible getter nodes, bounded per-run overrides, exact effective-value persistence in run history, and playback visibility. Descriptions are editorial help only and travel with shared workflow definitions; empty descriptions remain omitted. The execution package remains independent of the Studio, and run clients can opt into the additive `IParameterizedWorkflowClient` contract. Legacy schemas omit optional parameter fields, preserving their canonical JSON. Parameters are deliberately not secrets, mutable variables or arbitrary provider URLs. Additional exposed agent configuration pins, named entry-node inputs and richer data composition remain open parts of this milestone.

### Complete: MCP, skill, and agent authoring foundation

Studio supports local Streamable HTTP and stdio MCP discovery with visible compatibility reasons, explicit tool assignment, bounded nested JSON arguments, versioned text skills, per-agent system/user instructions, safe prompt interpolation, provider controls, and typed workflow parameters. Requirements and skill/profile snapshots survive package sharing; provider registrations, endpoints, executable settings, credentials, and host grants remain consumer-owned. Resource-bearing skills, additional model controls, and richer typed graph mapping remain planned. See [agent capability libraries](agent-capability-libraries.md), [agent profiles](agent-profiles.md), and [workflow parameters](workflow-parameters.md).

### Complete: bounded parallel agent teams

An agent node with two or more assigned profiles exposes a team tool. It fans the same bounded task out to each exact profile revision concurrently (maximum four), then returns a bounded JSON array of named profile outputs to the parent. Individual delegation and team calls share a four-child-execution limit per parent invocation. Each child retains its own tools and host authorization. Any member failure cancels siblings where possible and fails the team without returning a partial aggregate; external side effects cannot be undone. Audit writes are serialized to preserve event sequence under concurrency, and child model/tool activity is visible on the parent node with profile identity. See [agent profiles](agent-profiles.md).

### Complete: run-scoped coordination channels

Schema 1.12 adds up to eight pinned channel IDs per profile, with exact membership checks and bounded append-only messages. The current implementation supports manager-directed sequential delegate turns with a parent-invocation board and fixed parallel team calls with an isolated team-call board. Each message is limited to 4,000 characters, each channel to 32 messages and each board to 64. Every send/read is auditable, and playback does not repeat model calls. Deterministic tests cover membership, message exchange, ordering and isolation. Broader manager budgets, durable shared memory and graph-level scheduling remain separate work.

### Planned: collaborative parallel regions with shared channels and barriers

There is a limited related capability today: an Agent node assigned two or more versioned profiles exposes an optional `team_0` tool. If the parent model calls it, all assigned profiles work concurrently on the same task and can exchange messages through a board scoped to that team call. It is not a set of visible workflow branches: the parent may choose not to invoke it, and its result cannot be independently wired from each agent to graph nodes.

The target is to let users configure a bounded branch count on a parallel region, place profile-pinned Agent nodes on those branches, assign them to one collaboration group, and synchronize phases at explicit barriers. The proposed first configurable range is 2–8 branches, subject to host limits. Adding a branch exposes a stable branch ID/pin and leaves the draft visibly incomplete until its route and join wiring are authored; it must not silently connect arbitrary nodes. The first region contract should be a static acyclic graph with one stable participant identity per Agent invocation and no conditions, loops, or nested forks that could bypass or repeat a barrier. Each barrier generation requires exactly one arrival from each declared participant. Each member can read/write only its group's invocation-scoped bounded board. A barrier parks arrivals without holding host execution permits and admits remaining participants in bounded waves when the host cap is below group size. At release, capture an ordered message snapshot that all members of the next phase read consistently; writes from that phase become visible at the following barrier. After the final barrier, a normal join can collect named outputs and feed downstream nodes, including conditions, agents, or termination. Pin exact profile revisions and preserve each profile's skills, tools, provider settings, and host authorization.

This favors composing a visible graph region from parallel branches, Agent nodes, a shared collaboration-group setting, and barrier/join nodes over a monolithic team node. It keeps individual agent behavior and downstream paths editable, and lets deterministic work occur between agent phases. Before implementation, settle failure/partial-result, timeout, cancellation, retry/restart, external-side-effect, and budget-reservation policies; prove validation rejects bypassed or duplicate barrier arrivals; and test host concurrency caps below group size without deadlock. Audit must preserve invocation IDs, barrier generations, snapshot boundaries and observed message order so playback reconstructs what each participant saw without suggesting causal ordering or reinvoking agents. A dedicated Agent Team node is simpler but hides branch-level execution and downstream composition; keep it as a prefab/preset over the same serialized semantics, not a separate runtime model. This capability is not implemented today. See [agent profile teams and channels](agent-profiles.md) and [bounded parallel graphs](parallel-graphs.md).

### Completed: bounded JSON input, selection, and projection

Schema 1.9 provides a focused typed-data foundation: an entry node may validate bounded JSON input and pass its `json` port to an RFC 6901 property/array selector. Schema 1.13 adds a projection node with 1–16 named pointer mappings and allows bounded JSON output. Schema 1.15 lets projection fields assert their top-level JSON kind, and schema 1.16 adds recursive object and array-item mappings with optional fields and strict bounds. The selector emits text so existing agent, MCP and deterministic nodes remain unchanged; projected JSON can be reduced back to text with the selector. Core type-checks pins, graph edits preserve types, and runtime events record exact selected/projected values for playback. Loops and richer error/cancellation semantics remain planned.

### Completed: per-agent inference controls

Schema 1.10 adds a bounded output-token limit to each agent and an explicit reasoning-off preference. Defaults preserve the prior 1,024-token behavior; selected values are stored with the workflow and captured in model-request evidence. The Studio only exposes reasoning controls for providers that advertise support. Endpoint-specific limits or ignored options remain the provider's behavior.

### Complete: configurable parallel graph authoring and execution

The existing fixed two-branch blocks remain unchanged in older schemas. Schema 1.32 adds a static fork/anchor/join region with 2–8 ordered descriptors and stable branch IDs; the completed join is a bounded runtime JSON producer for downstream data consumers. Studio can create the region from a valid schema 1.31 graph, select its initial branch count, rename captions without changing IDs, and remove/undo the region. Authors can add Agent nodes to a branch and wire them to its matching join pin. The core validator rejects mismatched branch IDs, disconnected anchors, bypasses, overlapping branch paths, cross-join routes and unsupported branch structures. Runtime arrivals correlate by run, fork invocation and branch ID. Failure emits no partial value. The runner executes eight branches concurrently without losing shared evaluator updates. Existing schemas retain their serialization and execution paths. Studio cannot resize/reorder a populated region, and branch-local conditions, loops, nested regions, and standalone MCP nodes remain unsupported. Shared-channel agent phases and repeated barriers remain separate until their scheduling, timeout, retry, budget and partial-failure policies are defined. See [parallel graph behavior](parallel-graphs.md).

### Planned: broader complex graph orchestration

Explore nested forks, nested loops, and branch-local control structures after defining their cancellation, failure, audit, and replay semantics. These are executable semantics, not just canvas arrangements. Prove representative graphs against the pinned Microsoft Agent Framework adapter before fixing each node contract; preserve event ordering, exact-version dependencies, and truthful playback. Keep concurrency, payloads, and tool calls bounded and diagnosable.

### Later: host-attributed reconciliation decision records

After the agent-authoring priority, define explicit operator decisions for recorded uncertain outcomes, bound to the exact run/cursor/call identity with authenticated host-derived actors and append-only evidence. Distinguish a human assertion from a provider's actual response and preserve prior uncertainty. A decision must not silently retry external calls, rewrite history, grant tools, auto-approve or enable recovery of unsupported graphs. Observation stays read-only; production identity and authorization remain prerequisites for public controls.

### Planned: durable execution and security foundations

Define storage ownership, transactional run state/event capture, concurrency, checkpoint boundaries, recovery, and approval persistence. Test worker loss, duplicate delivery, shutdown, and uncertain external outcomes without assuming exactly-once side effects or silently retrying them.

Add workspace identity, backend authorization, authenticated audit actors, payload access/redaction, retention, encryption choices, quotas, and credential handling. Do not expose the current development host as a production service. This foundation precedes claims of dependable production operation or multi-tenant sharing.

## Completed milestone: folder-backed project execution and snapshot testing

Folder-backed projects now run saved executable workflows from a fresh manifest-validated in-memory capture, with dirty and stale editor state blocking the action. Restore points can run a selected workflow from their validated archive without restoring the active manifest. The execution path uses the existing LocalWorkflowRuntime and host-selected IWorkflowRunStore; run evidence stores the executable definition, captured layout, and project manifest/asset/layout/archive hashes. It does not write run history, credentials, grants, or provider configuration into the project. The editor accepts parameter overrides and shows the live/replay run viewer. Project-scoped catalog publication stays unsupported, external Git/editor writers are not locked, and runtime restart retains the existing interruption behavior. Core/UI regression tests and a Playwright smoke cover saved and restore-point execution. See [folder projects and Git source control](design.md#folder-projects-and-git-based-source-control), [project workspace behavior](project-workspaces.md), and [run storage](run-storage.md).

Rollout: (1) manifest and deterministic source files with stable IDs, layouts, and dependency hashes; **done**; (2) safe one-way catalog migration with a single authority and restart recovery; **done**; (3) multiple roots, project tabs, trees, and external edit conflict states; **done**; (4) bounded ignored `.vaw/` restore points with manual capture, preview, restore, deletion, sidecar-isolation checks, and in-memory workflow test runs; **done**; (5) selected-workflow `.vawpkg` export and clean consumer execution with host-supplied requirements and grants; **done**; (6) direct Studio execution of saved folder source with host-owned run evidence and exact source provenance; **done**. Catalog-backed project export, trusted publisher verification, and immutable deployment activation remain open. Mutable project-wide variables remain out of scope; workflow parameters remain workflow-owned.

### Planned: further reusable assets and specialization

Explicit conditional-block removal is implemented for isolated decision regions, with affected-node confirmation and shared definition/layout undo. Shared-boundary removal remains unsupported. Continue reusable workflow work with embedded child-graph drill-in and scoped composition controls. Preserve historical playback when sources/providers are absent.

### Planned: environment promotion

Keep provider endpoints, credentials, and MCP grants outside source-controlled project files. Define explicit environment binding references and promotion between development/test/production. Studio validates project files and reports unresolved asset references before build; consumers resolve bindings under their own policy. Multi-user editing and presence are out of scope.

### Planned: production qualification

Publish support boundaries only after repeatable recovery/security exercises, load and retention tests, observability/alerting, operational documentation, upgrade/migration checks, and an embedding sample against independently hosted services. Keep deterministic demonstrations and live-provider evidence labeled separately.

## Completed foundations

| Milestone | Delivered capability |
| --- | --- |
| Core contracts | Portable validated definitions, canonical hashes, immutable revisions, and compatibility tests |
| Framework execution | Real executors/edges, receiver-side transfer evidence, cancellation and bounded inputs |
| Local host and playback | Backend-owned queue, persisted local history, event stepping and payload inspection |
| Versioned catalog | Transactional SQLite drafts/revisions/audit, CAS conflicts, publication, restore and activation |
| Visual authoring | Reusable Razor editor, operation changes, insertion/reorder/removal, shared undo/redo |
| MCP and agents | Node-scoped broker, pinned MCP contracts, stable model connection IDs, actual framework agent/tool loop with deterministic fixtures |
| Graph canvas | Free placement, edge sockets, SVG routing, angle pins, comments/sections, versioned layouts and read-only playback |
| Quality and documentation | C#/Blazor/CSS/design skills, dependency boundaries, browser regression checks, architecture/user guides and locally validated Mermaid |
| Exact-action approvals | Schema-1.5 explicit MCP gates, bound requests and decisions, independent pause/cancel, duplicate/stale/owner-loss checks and deterministic vertical-slice UI proof |
| Conditional graphs | Schema-1.4 predicates and ports, exclusive merges, real conditional framework edges, immutable local graph edits and chosen-path playback |
| Consumer observation | Opt-in protected read API, HTTP observer, remote Studio attachment and reusable read-only panel with cursor/lifecycle/reconnect checks |
| Service integration | Standalone pinned-artifact execution, direct/queued MCP proof, host-stop verification, shared optional local-runtime DI registration and consumption contract |
| Reuse and sharing | Schema-1.3 origins, version-pinned expansion, section extraction, exact-version insertion and full design package controls with browser/capability tests |
| Typed branch topologies | Schema-1.26 bounded decision DAGs, nested/reconvergent branches, multiple success/failure exits, path audit and recorded playback |
| Pure graph-function runtime | Schema-1.27 hash-pinned string/Boolean dataflow functions, lazy evaluation, package requirement, bounded nested-value audit evidence |

Detailed tests, limitations, and historical rollout notes are in [implementation history](implementation-history.md).

## Keep the roadmap current

Update this document with each milestone: mark only verified work complete, identify the active increment, record new prerequisites, and link evidence/details from the owning docs. Move retrospective detail into implementation history. Keep architecture and user instructions current in the same change; do not duplicate their full contents here. Do not invent dates or count all roadmap features as one production commitment.
