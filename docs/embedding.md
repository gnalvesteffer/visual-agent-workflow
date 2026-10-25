# Embedding the initial components

Designer embedding and engine embedding are independent. This page covers reusable UI; a user-owned service can consume execution libraries without any designer or Studio process. See [service integration](service-integration.md) for existing lower-level APIs and the planned hosting SDK.

Reference `src/VisualAgentWorkflow.Blazor/VisualAgentWorkflow.Blazor.csproj` from a Blazor application. The Razor library depends on Core and ASP.NET Core rendering support, not on the framework adapter or local runtime.

Include its optional stylesheet in the host document:

```html
<link rel="stylesheet" href="_content/VisualAgentWorkflow.Blazor/workflow.css" />
```

To show recorded evidence, supply a `RunSnapshot`:

```razor
@using VisualAgentWorkflow.Core
@using VisualAgentWorkflow.Blazor.Components

<WorkflowRunViewer Snapshot="RecordedRun" />

@code {
    [Parameter] public RunSnapshot? RecordedRun { get; set; }
}
```

The viewer reads the supplied snapshot only. It does not resolve a runtime or invoke tools, models, or assemblies. An executable rendering test verifies this without registering any runtime/client service. The parent decides how to obtain and authorize snapshots and whether to refresh them for live observation.

For the initial template runner and history sidebar, supply a host implementation of `IWorkflowClient`:

```razor
@using VisualAgentWorkflow.Core
@using VisualAgentWorkflow.Blazor.Components
@inject IWorkflowClient Client

<WorkflowStudio Client="Client" />
```

The host must configure an interactive render mode for event handlers, serve Razor static assets, and register its client implementation. Client methods start a backend-owned run, fetch snapshots/history, or explicitly cancel; a component's disposal stops observation only. A remote execution/control implementation remains host-owned. For read-only remote observation, `HttpWorkflowRunObserver` and `WorkflowObservationPanel` are implemented; see [consumer observation](observation.md).

To include the saved-workflow designer, register an `IWorkflowCatalog` and an `IPublishedWorkflowClient`, then pass the catalog to the studio:

```razor
@inject IPublishedWorkflowClient PublishedClient
@inject IWorkflowCatalog Catalog

<WorkflowStudio Client="PublishedClient" Catalog="Catalog" />
```

Alternatively embed `WorkflowDesigner` directly with `Catalog`, `Client` (an `IPublishedWorkflowClient`), and an optional `OnRunStarted` callback. It manages local edits and calls the host's catalog for persistence/publication. `PipelineEditor` is a smaller controlled component taking `Definition`, `DefinitionChanged`, `ReadOnly`, and `Disabled`. Its callbacks produce immutable definitions; the host decides how to persist them. Neither component depends on SQLite or the local hosting assembly.

The designer currently supports the approved string-graph subset: conditional block insertion, predicate/arm editing, exclusive merges, insert/remove transformations, select uppercase/reverse or MCP operations, reorder with keyboard-accessible buttons, undo/redo, save/publish, version preview/comparison/restore/activation, selected-version execution, and definition JSON import/export. Import retains the current workflow identity and does not publish or execute automatically. The editor explicitly disables switching workflows while local changes are pending. Conflicts preserve local edits and require an explicit reload/discard.

Supply an optional `IToolBindingCatalog` through `WorkflowStudio.ToolCatalog` or `WorkflowDesigner.ToolCatalog` to populate MCP binding choices. `PipelineEditor.Bindings` accepts descriptors directly. These are authoring choices, not grants: backend execution must resolve a scope through `INodeToolBroker` and enforce `IToolAuthorizationPolicy`. The built-in Studio supplies three pure development-fixture tools.

Supply `IAgentProviderCatalog` through `WorkflowStudio.AgentCatalog` or `WorkflowDesigner.AgentCatalog` for agent node choices; `PipelineEditor.Providers` accepts descriptors directly. Agent nodes expose instructions and checkboxes for their tool requirements. The backend resolves only the pinned host provider version and intersects those requirements with host policy. The built-in provider is explicitly deterministic, not a live LLM. See [runtime configuration](runtime-configuration.md) before integrating provider factories.

The standalone Studio demonstrates Interactive Server hosting and a singleton local background runtime. It is a development host, not a production integration template. Hosts own authentication, payload access, storage, transport, and lifecycle. The component polls history every 500 ms and displays at most 50 run-history entries. Its canvas supports freely positioned nodes, rerouted wires, labels/comments, and named sections; arbitrary rewiring, parallel branches and loops remain unsupported. Playback supports stepping and scrubbing; timed playback and speed controls remain planned.

## Component lifecycle and theming

Changing `Client` or `Catalog` reloads the corresponding component context and ignores late results from the previous context. Changing only tool/provider catalogs refreshes choices without discarding draft edits. Disposal stops observation and pending UI waits; it does not cancel backend runs. History polling starts after interactive rendering and retains action errors independently of polling success.

Library CSS is scoped to component roots. Set inherited public properties on a wrapping element to theme nested components: `--vaw-font-family`, `--vaw-color-scheme`, and `--vaw-color-{canvas,surface,control,text,muted,border,control-border,focus,accent,primary,success,error,warning}`. Use accessible color combinations. The Studio's page background and body reset live in its separate `studio.css`; consumers retain their own page styles. Layout responds to the component container, including narrow embeds on wide screens.

Hosts may wrap the components in `WorkflowErrorBoundary` with their own `ErrorContent` and recovery action. It logs exception types without raw exception messages; the Studio demonstrates a generic fallback and full-page reload.

Interactive Server hosts must align bounded editor inputs with SignalR transport limits. Field events can contain the text more than once. The development Studio allows messages up to 8 MiB so its JSON editor can carry a bounded 1 MiB definition; HTTP body and domain limits remain independent. Production hosts must choose transport budgets alongside their concurrency and payload policies.

The standalone Studio uses a flat light shell and a dark graph canvas; the embeddable library preserves host theme inheritance. Set `--vaw-color-*`, `--vaw-font-family`, `--vaw-radius-panel`, `--vaw-radius-control`, and `--vaw-font-mono` on a component ancestor to theme the library. Graph colors have separate viewport-scoped overrides, including `--vaw-graph-color-canvas`, `--vaw-graph-color-surface`, `--vaw-graph-color-control`, `--vaw-graph-color-text`, `--vaw-graph-color-muted`, `--vaw-graph-color-border`, `--vaw-graph-color-control-border`, `--vaw-graph-color-focus`, `--vaw-graph-color-accent`, `--vaw-graph-color-success`, `--vaw-graph-color-error`, `--vaw-graph-color-warning`, and `--vaw-graph-color-grid`. Graph positions are stable across container sizes; the grid fills its viewport and tracks pan/zoom, while the graph viewport scrolls inside narrow embeds and the inspector reflows. Definition-role colors do not imply execution state. The library does not alter unrelated host elements.

`PipelineEditor.GraphEdited` optionally emits a `WorkflowGraphEdit` (definition + layout) for atomic local history; when supplied it handles semantic edits instead of `DefinitionChanged`/`LayoutChanged`. Without it, existing callbacks remain supported. `EditorActions` places host controls inside the graph toolbar. `WorkflowGraphCanvas.NodeInspector`, `ToolbarContent`, controlled `SelectedNodeId`/`SelectedNodeIdChanged`, `NodeLibrary`, `ContextMenu`, `NodeCreateRequested`, `DeleteNodeRequested` and `ConnectionSelected` are optional workbench hooks. Existing `NodeContent` and built-in annotation controls still work without these slots. For dynamic string parameters use Razor expressions, for example `SelectedNodeId="@selectedId"`.

Native fullscreen includes the graph toolbar and panels, requires iframe fullscreen permission, and uses browser-local user activation. Context menus use the modern Popover API; the searchable library remains available independently. `--vaw-node-width` and `--vaw-inspector-width` configure card and property widths. Selection, fullscreen, pan and zoom are not serialized or execution controls.

`PipelineEditor.Layout` and `LayoutChanged` expose controlled presentation state independently of `DefinitionChanged`. Without a supplied layout it keeps local presentation state for the component lifetime. `WorkflowGraphCanvas` is the reusable canvas with `Definition`, `Layout`, `LayoutChanged`, `NodeContent`, `ReadOnly`, and `Disabled`; it imports its own `graphCanvas.js` ES module after interactive rendering, so hosts do not need a global script tag. Assets must be served from the Razor library as usual. Disposal removes pointer handlers, resize observation, JS references, and scheduled frames; it never stops a backend run.

`WorkflowDesigner` automatically uses `IWorkflowDesignCatalog` when implemented by its `Catalog`. That optional extension saves definition/layout atomically under the same generation. Legacy hosts keep definition editing, with position/annotation editing disabled rather than silently losing it. `WorkflowRunViewer.Layout` accepts the recorded revision's layout; the Studio resolves it from the catalog. Standalone viewers default to generated positions when no layout is supplied. See [graph controls and persistence](graph-editor.md).

`WorkflowDesigner` also exposes reusable section extraction and published-pipeline insertion through its supplied `IWorkflowCatalog`/`IWorkflowDesignCatalog`; no extra host service is required. Source requirements are shown through the supplied tool/provider catalogs, without treating listed registrations as grants. `PipelineReuse` and `WorkflowPackageJson` are Core APIs for consuming applications that want their own authoring/sharing UI. Reuse and package imports enter the existing combined definition/layout undo history. Source origin metadata in schema 1.3 is informational and never triggers runtime code loading or authorization.

## Optional pause/resume capability

The development host supports cooperative node-boundary control through `IWorkflowRunControl` / `IPublishedWorkflowRunControl`, paused starts, and HTTP pause/resume routes. Existing client interfaces remain supported. Controls preserve the same live run and pinned revision; browser disposal does not resume or cancel it. Host restart interrupts unfinished continuations. See [run-control semantics and requests](run-control.md).

## Read-only consumer observation

Embed `<WorkflowObservationPanel Observer="Observer" SourceLabel="Service workflows" />` with a host-supplied `IWorkflowRunObserver`. Use the local journal reader or the separate HTTP observer client; no Studio or mutation-capable workflow client is needed. The panel provides recent runs, polling, graph inspection and recorded playback. Parent hosts own authentication, visible runs and payload access. Dispose/rebind cancels observation waits only. See [consumer observation setup](observation.md).

## Optional approval controls

`WorkflowRunViewer` accepts optional `ApprovalClient` (`IWorkflowApprovalClient`) and `ApprovalChanged` callback parameters. Without that client it renders approval evidence read-only, as the remote observation panel does. Controls require the latest live position; recorded playback cannot decide an old request. `WorkflowApprovalPanel` can also be embedded directly with a snapshot, host-authorized client and explicit `CanDecide`. Hosts must supply authentication/authorization and server-derived attribution. See [approvals](approvals.md).


## Recovery assessment display

`WorkflowObservationPanel` automatically uses the optional `IWorkflowRunReconciliationObserver` capability and displays latest evidence through `WorkflowReconciliationPanel`, with no recovery/retry controls. A consumer can embed that component directly with its `Assessment` parameter. It needs no concrete runtime and HTML-encodes explanations. Cursor/source failures suppress eligibility while leaving recorded graph evidence available. See [read-only reconciliation](run-reconciliation.md).
