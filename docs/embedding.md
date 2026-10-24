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

The standalone Studio demonstrates Interactive Server hosting and a singleton local background runtime. It is a development host, not a production integration template. Hosts own authentication, payload access, storage, transport, and lifecycle. The component currently polls history every 500 ms, displays at most 50 history entries, and supports pipeline templates rather than arbitrary visual graph editing. Playback supports stepping and scrubbing; timed playback and speed controls remain planned.
