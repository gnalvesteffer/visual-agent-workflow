# Implemented architecture

The engine and designer are independently consumable libraries; Studio is their optional IDE and development composition. User-owned service hosting is a first-class product goal, with direct execution APIs and a local hosting helper already available; a production SDK and stable package distribution remain planned. See [service integration](service-integration.md).

The Studio is a .NET 10 Interactive Server development host that composes embeddable Blazor components, a SQLite workflow catalog, a background run queue, Microsoft Agent Framework adapters, and a node-scoped MCP broker. Execution belongs to the backend. A browser edits definitions and observes recorded evidence; closing it does not cancel a run.

This page describes shipped code. The broader [product design](design.md) includes planned distributed recovery, workspace authorization, parallelism, collaboration, extensions, and reusable assets. Those are not implied by these diagrams.

## Package boundaries

The arrows below mean project dependencies, not network requests. Core has no external package dependencies. The Razor library depends on Core and rendering support, not the concrete runtime, SQLite, or MCP transport. Its CSS and graph assets are local and self-contained; theming is scoped to the component and inherited tokens without changing unrelated host controls. Studio chooses concrete services at the composition root. Its local Settings page can create, list, version, and delete OpenAI-compatible Chat Completions providers in a version-aware executor; API keys are protected in the Studio data directory and omitted from descriptors. Deletion removes a locally saved registration without checking workflow references. This local management surface inherits the development host’s loopback-only, unauthenticated boundary. Architecture tests enforce these inward boundaries.

```mermaid
flowchart TB
    Studio["Studio: application composition"] --> Blazor["Blazor: editor and playback components"]
    Studio --> Hosting["Hosting: catalog and background runtime"]
    Studio --> Agents["Agent Framework adapter"]
    Studio --> Providers["Optional OpenAI provider adapter"]
    Providers --> Agents
    Providers --> Core
    Studio --> Mcp["MCP adapter"]
    Studio --> Http["Observation HTTP client"]
    Http --> Core
    Hosting --> Agents
    Blazor --> Core["Core: definitions, layouts, revisions and interfaces"]
    Hosting --> Store["Host-selected run store: file or SQLite"]
    Hosting --> Core
    Agents --> Core
    Mcp --> Core
```

## Editing and versioning

`WorkflowDefinition` is executable JSON. `WorkflowLayout` is separate presentation metadata for positions, labels, comments, sections, and reroutes. Layout-only edits do not change the executable content hash. The designer keeps local immutable edits and a shared undo/redo history until the user explicitly saves. The searchable library, contextual palette and output-pin creation all call validated Core graph operations. An additive UI-only `WorkflowGraphEdit` combines semantic and placement changes into one history entry; it changes no execution or artifact contract. Selected properties, zoom, workspace mode and native fullscreen are view state. Designer and run-inspector views share the same embeddable components and remain independent of backend run ownership.

Definition serialization sorts ID-keyed maps and normalizes indented JSON line endings to LF before hashing, so content hashes remain stable across Windows and Unix.

`IWorkflowDesignCatalog` extends the original catalog without breaking its methods. A design save writes definition, layout, generation, and audit entries in one SQLite transaction. A stale generation returns a conflict without overwriting either document. Publication captures an immutable definition/revision and layout; restoring copies a historical version into the draft. Activation selects which revision new runs use.

```mermaid
sequenceDiagram
    actor Author
    participant Editor as Workflow designer
    participant Catalog as Design catalog
    participant DB as SQLite
    Author->>Editor: Edit graph or annotations locally
    Author->>Editor: Save draft
    Editor->>Catalog: Definition, layout and expected generation
    Catalog->>DB: Begin transaction and check generation
    alt Generation matches
        Catalog->>DB: Save documents and audit, increment generation
        DB-->>Catalog: Commit
        Catalog-->>Editor: Saved draft and layout
    else Another editor saved first
        DB-->>Catalog: Roll back
        Catalog-->>Editor: Conflict, preserve local edits
    end
```

The database schema is version 2. Schema 1 databases migrate by adding layout tables. Older revisions without a captured layout receive deterministic default positions. The definition schema remains independent from the database and layout schemas. Schema 1.6 optionally adds up to 64 user-defined string parameters with stable IDs, display names, and portable defaults; absent optional JSON members remain omitted so existing definition hashes are unchanged. A `core.workflow-value` node explicitly replaces its incoming string with one resolved parameter value.

Parameter overrides belong to a run, never its immutable published definition. The host resolves defaults and bounded caller overrides before enqueueing, rejects unknown IDs, and stores the complete effective string map in the run header for audit/playback. Workflow packages carry parameter contracts and defaults, but not run overrides or credentials. Values are not secret: consumers should not put credentials or sensitive payloads into parameters because current local run stores record them in plaintext.

## Execution and observation

A published run resolves the exact stored revision before enqueueing. The local background worker translates validated linear/conditional string graphs to real Agent Framework executors and edges. Workflow value nodes read the run's resolved parameter map and emit the value through ordinary node/transfer evidence. A maximum of 32 process-owned contexts share one active execution permit. Paused continuations release the permit while retaining their live framework state and admission slot. A per-run writer sequences control and executor events together; pause is acknowledged between nodes, not inside an admitted agent/tool operation. See [pause and resume](run-control.md). Actual receiver-side transfers, node invocations, model rounds, tool calls, and terminal outcomes become recorded events. The viewer reconstructs those events without calling executors, models, or tools.

```mermaid
flowchart LR
    Editor["Studio or embedded client"] -->|"Start exact revision"| Queue["Backend-owned bounded queue"]
    Queue --> Worker["Local background worker"]
    Worker --> Framework["Agent Framework executors"]
    Framework --> Broker["Node-scoped MCP broker"]
    Broker --> Tools["Host-registered MCP servers"]
    Framework --> Journal["Host-selected run store"]
    Broker --> Journal
    Journal -->|"Recorded snapshots and events"| Viewer["Live observation and playback"]
```

Linear and acyclic conditional string graphs are executable today. Schema 1.6 adds workflow-defined string parameters and getter nodes; it does not add arbitrary data types or mutable variables. Conditions select one string output and exclusive merges pass through the selected value; they are not parallel join barriers. See [conditional graph semantics](conditional-graphs.md). Uppercase/reverse are deterministic operations. The host's `demo.scripted` agent exercises the framework tool loop but does not interpret instructions or contact an LLM. The optional OpenAI provider adapter supplies host-configured, versioned factories through the official SDK and AI bridge; protocol tests cover its path, while credentialed live-service qualification remains outstanding. Core and Blazor have no provider SDK dependency. See [model providers](model-providers.md). MCP bindings are pinned requirements that must also pass host policy on every invocation. Tool/provider catalogs are authoring choices, not permissions.

Schema 1.7 optionally embeds reviewed text-only skill revisions and per-agent structured user-message parts. The agent's existing `instructions` remain the system message; the user-message template resolves only incoming text and declared string parameters. A run records the resolved model request and exact assigned skill IDs, versions, and content hashes. Imported skill text cannot grant MCP access. Studio's local Skill library manages revisions and folders, while the published definition carries the assigned revision text; a consumer service can execute it without Studio. This is a bounded text capability, not arbitrary skill scripts or remote skill resources.

Studio's local MCP library can connect Streamable HTTP endpoints, discover tools with one `text` string input, and explicitly enable them. Its endpoint and protected bearer token remain in Studio storage. Workflow definitions and packages contain only logical tool IDs and pinned contract hashes; a consumer must register matching connections and host policy itself. Folder paths are authoring metadata. The broker checks a node's requested contract and effective host policy before invocation, including tools exposed to an agent. Configured servers run under the local Studio operator's trust boundary; they are not a sandbox or a production tenant boundary.

```mermaid
flowchart LR
    Studio["Studio MCP and skill libraries"] --> Draft["Agent selections in draft"]
    Draft --> Package["Portable published definition"]
    Package --> Host["Consumer host resolves MCP bindings and policy"]
    Package --> Skill["Embedded exact skill text"]
    Host --> Run["Backend agent execution"]
    Skill --> Run
    Run --> Evidence["Recorded model and tool evidence"]
```

## Persistence and failure boundaries

SQLite stores drafts, immutable revisions, layouts, and catalog audit. The separate `IWorkflowRunStore` persists each run's pinned definition and execution evidence. The default file implementation publishes complete snapshots atomically; the optional SQLite implementation commits event batches with sequence/status in one transaction. Runtime/controller and read-only observers can share a host-selected store. Catalog and run stores do not share a transaction. Runtime ownership is exclusive even with SQLite concurrency checks; the ordinary local runtime interrupts unfinished runs after restart. A separate opt-in checkpoint host can explicitly rehydrate deterministic graphs saved in a suspended state. Historical payload viewing makes no live calls. See [run storage](run-storage.md) for legacy journal compatibility, store selection and failure boundaries.

The development host permits only loopback/same-origin access and refuses non-Development startup. These checks are containment, not authentication. Audit actors are development placeholders; captured text is plaintext. There is no workspace authorization, distributed ownership, transactional crash repair, encryption/redaction, approval durability, extension sandbox, or production readiness claim. See [runtime configuration](runtime-configuration.md) and [roadmap and current scope](roadmap.md) before integrating a host.

## Reuse and sharing contracts

Core supports [version-pinned pipeline expansion and portable packages](reusable-pipelines.md). Expansion creates ordinary processing nodes with fresh identities and optional schema-1.3 origin metadata. It adds no runtime source loader or new project dependency. Source updates do not mutate a consumer, and imported provenance does not authenticate an author. Exact MCP/provider requirements travel with copied nodes; host grants do not. Studio uses the same workflow catalog for reusable drafts and publications. Extraction creates a separate draft; insertion is a single local immutable composition edit, followed by normal save/publication. No additional database schema or runtime service is introduced. Package exports read a consistent saved design or an immutable publication, and import only stages local definition/layout edits. Requirement listings report registration availability rather than authorization.

## Headless service integration boundary

`VisualAgentWorkflow.Core` and `VisualAgentWorkflow.AgentFramework` do not reference the Blazor library or Studio. `WorkflowJson`/`WorkflowPackageJson`, `WorkflowRevision`, and `PipelineRunner.ExecuteAsync` already provide lower-level artifact loading and execution. The caller supplies tool/agent adapters, an event sink, run identity, and cancellation. The current `LocalWorkflowRuntime`, file journal, and SQLite catalog are optional development-host building blocks rather than a production service SDK.

The standalone consumer proof uses these public APIs without a Studio process, browser connection, or renderer. `AddLocalWorkflowRuntime` now provides shared lazy DI registration for that sample and Studio, without creating a catalog or UI dependency. Studio explicitly adds publication-aware clients and its catalog. A stable production SDK, packaged distribution, richer typed contracts, production persistence and durable recovery remain planned. See [local hosting API](hosting-sdk.md).

## Standalone consumer

`samples/WorkflowService` references Hosting/MCP and their inward dependencies, with no Blazor/Studio reference. It loads a reviewed package and reconstructs the operator-configured exact revision, resolves only host-registered dependencies, and executes directly or through the local queue. Its direct/queued modes create no authoring catalog or HTTP listener; SQLite run persistence is optional; optional `serve` mode explicitly enables the separate observation endpoint. Its journal directory has one process owner; stopped/unfinished work is cancelled or interrupted, not crash-resumed. See [service integration](service-integration.md).

## Consumer observation

Core owns the read-only `IWorkflowRunObserver` contract, summaries and cursor pages. Hosting can explicitly map a policy-protected GET-only API over a host-supplied observer; registering a runtime does not enable it. Observation.Http depends only on Core and implements bounded HTTP reads. Studio server configuration supplies named remote sources and credentials, while `WorkflowObservationPanel` in Blazor consumes only the observer contract. A consumer can embed that same panel directly over its journal or an HTTP observer. Read requests never enter execution or control services. The development example provides single-operator bearer authentication; production identity, per-run visibility and access audit remain host concerns. See [consumer observation](observation.md) for limits and data flow.

## Approval gates

Schema 1.5 approval nodes immediately precede a pinned MCP tool. The adapter captures the exact input/tool/revision plus a reserved target invocation ID. The process-owned controller persists the request, releases its execution permit while waiting, sequences a host-attributed decision and reacquires the permit only when approved and not paused. The next tool invocation consumes the reserved identity and still passes broker authorization. The browser owns no continuation, and the separate observation API gains no mutation authority. Requests persist as evidence, not crash-restorable checkpoints. See [approvals](approvals.md).


## Deterministic checkpoint host

`CheckpointWorkflowHost` is a separate opt-in consumer API over SQLite and the adapter's native Agent Framework JSON checkpoint path. It commits bounded native state, capture/suspension evidence and the current status together; explicit recovery validates the exact latest checkpoint/cursor and rebuilds identical executors without replaying completed nodes. Only deterministic linear/conditional graphs are admitted. It does not change Studio's process-owned queue, and model/MCP/approval recovery remains excluded. Both built-in stores now provide an exclusive local owner handle honored by the ordinary runtime and checkpoint host, while observers read independently. Run-database schema 2 adds checkpoint storage; the authoring catalog is unchanged. See [recovery scope and ownership](checkpoint-recovery.md).


## Read-only recovery disposition

Core's optional `IWorkflowRunReconciliationObserver` supplies cursor-bound metadata without changing the original observer contract. A pure work projection preserves unsettled model/tool/approval facts even after terminal status; new model/tool call IDs distinguish repeated calls without changing event envelopes. Hosting combines that evidence with checkpoint validation; SQLite reads both under one transaction. The protected observation group adds only a GET assessment route, and the Core-only HTTP client plus embeddable reconciliation panel validate identity/cursor alignment. The observation panel suppresses stale eligibility on source changes or outage and keeps assessment independent of playback. It neither grants ownership nor triggers execution or operator decisions. See [run reconciliation](run-reconciliation.md).
