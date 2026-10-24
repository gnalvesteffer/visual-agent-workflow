# Embedding the initial components

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

The host must configure an interactive render mode for event handlers, serve Razor static assets, and register its client implementation. Client methods start a backend-owned run, fetch snapshots/history, or explicitly cancel; a component's disposal stops observation only. A remote implementation can call a separate backend, but no packaged HTTP client is implemented yet.

To include the saved-workflow designer, register an `IWorkflowCatalog` and an `IPublishedWorkflowClient`, then pass the catalog to the studio:

```razor
@inject IPublishedWorkflowClient PublishedClient
@inject IWorkflowCatalog Catalog

<WorkflowStudio Client="PublishedClient" Catalog="Catalog" />
```

Alternatively embed `WorkflowDesigner` directly with `Catalog`, `Client` (an `IPublishedWorkflowClient`), and an optional `OnRunStarted` callback. It manages local edits and calls the host's catalog for persistence/publication. `PipelineEditor` is a smaller controlled component taking `Definition`, `DefinitionChanged`, `ReadOnly`, and `Disabled`. Its callbacks produce immutable definitions; the host decides how to persist them. Neither component depends on SQLite or the local hosting assembly.

The designer currently supports the approved linear string-pipeline subset: insert/remove transformations, select uppercase/reverse operations, reorder with keyboard-accessible buttons, undo/redo, save/publish, version preview/comparison/restore/activation, selected-version execution, and definition JSON import/export. Import retains the current workflow identity and does not publish or execute automatically. The editor explicitly disables switching workflows while local changes are pending. Conflicts preserve local edits and require an explicit reload/discard.

The standalone Studio demonstrates Interactive Server hosting and a singleton local background runtime. It is a development host, not a production integration template. Hosts own authentication, payload access, storage, transport, and lifecycle. The component polls history every 500 ms and displays at most 50 run-history entries. It has no free-form canvas, arbitrary wiring, or branches yet. Playback supports stepping and scrubbing; timed playback and speed controls remain planned.
