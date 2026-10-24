using System.Text;
using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowDesigner
{
    private string workflowFilter = "";
    [Parameter, EditorRequired] public IWorkflowCatalog Catalog { get; set; } = default!;
    [Parameter, EditorRequired] public IPublishedWorkflowClient Client { get; set; } = default!;
    [Parameter] public EventCallback<RunSnapshot> OnRunStarted { get; set; }
    [Parameter] public IToolBindingCatalog? ToolCatalog { get; set; }
    [Parameter] public IAgentProviderCatalog? AgentCatalog { get; set; }

    private sealed record Binding(IWorkflowCatalog Catalog, IPublishedWorkflowClient Client, CancellationToken Token);
    private Binding? binding;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? contextLifetime;
    private CancellationTokenSource? choicesLifetime;
    private IToolBindingCatalog? loadedTools;
    private IAgentProviderCatalog? loadedAgents;
    private long choicesVersion;
    private bool disposed;
    private IReadOnlyList<ToolBindingDescriptor> bindings = [];
    private IReadOnlyList<AgentProviderDescriptor> providers = [];
    private WorkflowDefinition? definition;
    private WorkflowLayout? layout;
    private WorkflowLayout? previewLayout;
    private string? layoutHash;
    private sealed record DesignState(WorkflowDefinition Definition, WorkflowLayout Layout);
    private long generation;
    private Guid? activeRevisionId;
    private Guid? selectedRevisionId;
    private PublishedWorkflowRevision? preview;
    private IReadOnlyList<WorkflowDraft> workflows = [];
    private IReadOnlyList<PublishedWorkflowRevision> versions = [];
    private IReadOnlyList<WorkflowAuditEntry> audit = [];
    private readonly Stack<DesignState> undo = new();
    private readonly Stack<DesignState> redo = new();
    private string? definitionHash;
    private string? savedHash;
    private string? DesignHash => definitionHash is null ? null : definitionHash + ":" + layoutHash;
    private bool dirty => definition is not null && DesignHash != savedHash;
    private bool busy;
    private bool interactive;
    private string? error;
    private string? message;
    private string jsonText = "";
    private string runInput = "Hello, workflows!";
    private bool startPublishedPaused;
    private WorkflowDefinition? downloadedDefinition;
    private string downloadHref = "";

    private string DownloadHref
    {
        get
        {
            var viewed = preview?.Revision.Definition ?? definition;
            if (viewed is not null && !ReferenceEquals(viewed, downloadedDefinition))
            {
                downloadHref = "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkflowJson.Serialize(viewed)));
                downloadedDefinition = viewed;
            }
            return downloadHref;
        }
    }

    private WorkflowLayout? downloadedLayout;
    private WorkflowDefinition? layoutDownloadDefinition;
    private string layoutDownloadHref = "";
    private string LayoutDownloadHref
    {
        get
        {
            var viewed = preview?.Revision.Definition ?? definition;
            var presentation = preview is not null ? previewLayout ?? WorkflowLayout.Create(preview.Revision.Definition) : layout;
            if (viewed is not null && presentation is not null &&
                (!ReferenceEquals(viewed, layoutDownloadDefinition) || !ReferenceEquals(presentation, downloadedLayout)))
            {
                layoutDownloadHref = "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkflowLayoutJson.Serialize(presentation, viewed)));
                layoutDownloadDefinition = viewed; downloadedLayout = presentation;
            }
            return layoutDownloadHref;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (disposed) return;
        if (binding is not null && ReferenceEquals(binding.Catalog, Catalog) && ReferenceEquals(binding.Client, Client))
        {
            if (!ReferenceEquals(loadedTools, ToolCatalog) || !ReferenceEquals(loadedAgents, AgentCatalog))
                await Choices(binding);
            return;
        }

        var previous = contextLifetime;
        contextLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        binding = new(Catalog, Client, contextLifetime.Token);
        previous?.Cancel();
        previous?.Dispose();
        definition = null; layout = null; previewLayout = null; layoutHash = null; definitionHash = null; savedHash = null;
        workflows = []; versions = []; audit = []; bindings = []; providers = [];
        preview = null; selectedRevisionId = null; activeRevisionId = null;
        undo.Clear(); redo.Clear(); jsonText = ""; runInput = "Hello, workflows!"; startPublishedPaused = false; busy = false;
        loadedTools = null; loadedAgents = null; ResetReuse();
        await Perform(async current =>
        {
            var drafts = await current.Catalog.ListDraftsAsync().WaitAsync(current.Token);
            if (!Current(current)) return;
            if (drafts.Count > 0) await Load(drafts[0], current); else NewWorkflow();
            if (Current(current)) await Choices(current);
        });
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && !disposed) { interactive = true; StateHasChanged(); }
    }

    private bool Current(Binding current) => !disposed && ReferenceEquals(binding, current);

    private void NewWorkflow()
    {
        ResetReuse();
        var next = PipelineEditing.Remove(PipelineTemplates.Create("uppercase"), "step-1") with { WorkflowId = "workflow-" + Guid.NewGuid().ToString("N") };
        SetDesign(next, WorkflowLayout.Create(next));
        generation = 0; activeRevisionId = null; selectedRevisionId = null; preview = null;
        versions = []; audit = []; undo.Clear(); redo.Clear(); jsonText = "";
        savedHash = DesignHash;
        error = null; message = "New workflow created locally. Save its draft before publishing.";
    }

    private void SetDesign(WorkflowDefinition value, WorkflowLayout presentation)
    {
        definition = value; layout = presentation;
        definitionHash = WorkflowJson.ComputeHash(value);
        layoutHash = WorkflowLayoutJson.ComputeHash(presentation, value);
        if (!presentation.Sections.ContainsKey(reusableSectionId)) reusableSectionId = "";
        if (!value.Nodes.TryGetValue(insertAfterNode, out var after) || after.Type == NodeTypes.Output) insertAfterNode = value.EntryNodeId;
    }

    private async Task Load(WorkflowDraft draft, Binding current, bool resetHistory = true)
    {
        var design = current.Catalog is IWorkflowDesignCatalog designs
            ? await designs.GetDesignAsync(draft.Definition.WorkflowId).WaitAsync(current.Token) : null;
        if (design is not null) draft = design.Draft;
        var metadata = await ReadMetadata(current, draft.Definition.WorkflowId);
        if (!Current(current)) return;
        SetDesign(draft.Definition, design?.Layout ?? WorkflowLayout.Create(draft.Definition));
        generation = draft.Generation; activeRevisionId = draft.ActiveRevisionId;
        savedHash = DesignHash;
        ApplyMetadata(metadata);
        if (resetHistory)
        {
            preview = null; undo.Clear(); redo.Clear(); jsonText = "";
            selectedRevisionId = draft.ActiveRevisionId ?? draft.HeadRevisionId;
        }
    }

    private sealed record Metadata(IReadOnlyList<WorkflowDraft> Workflows,
        IReadOnlyList<PublishedWorkflowRevision> Versions, IReadOnlyList<WorkflowAuditEntry> Audit);

    private static async Task<Metadata> ReadMetadata(Binding current, string id) => new(
        await current.Catalog.ListDraftsAsync().WaitAsync(current.Token),
        await current.Catalog.ListRevisionsAsync(id).WaitAsync(current.Token),
        await current.Catalog.ReadAuditAsync(id).WaitAsync(current.Token));

    private void ApplyMetadata(Metadata metadata)
    { workflows = metadata.Workflows; versions = metadata.Versions; audit = metadata.Audit; }

    private async Task Choices(Binding current)
    {
        var tools = ToolCatalog;
        var agents = AgentCatalog;
        loadedTools = tools; loadedAgents = agents;
        var version = ++choicesVersion;
        choicesLifetime?.Cancel();
        choicesLifetime?.Dispose();
        choicesLifetime = CancellationTokenSource.CreateLinkedTokenSource(current.Token);
        var token = choicesLifetime.Token;
        try
        {
            IReadOnlyList<ToolBindingDescriptor> newBindings = tools is null ? [] : await tools.ListBindingsAsync(token).WaitAsync(token);
            IReadOnlyList<AgentProviderDescriptor> newProviders = agents is null ? [] : await agents.ListProvidersAsync().WaitAsync(token);
            if (!Current(current) || version != choicesVersion) return;
            bindings = newBindings; providers = newProviders;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(current) && version == choicesVersion)
                error = "Could not load node provider choices. The current draft is preserved; retry the provider catalog.";
        }
    }

    private void Changed(WorkflowDefinition next)
    {
        if (definition is null || layout is null || WorkflowJson.ComputeHash(next) == definitionHash) return;
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(next, layout.Reconcile(next)); error = null; message = null;
    }

    private void ChangedGraph(WorkflowGraphEdit next)
    {
        if (definition is null || layout is null) return;
        if (WorkflowJson.ComputeHash(next.Definition) == definitionHash && WorkflowLayoutJson.ComputeHash(next.Layout, next.Definition) == layoutHash) return;
        WorkflowValidator.ThrowIfInvalid(next.Definition);
        WorkflowLayoutJson.Validate(next.Layout, next.Definition);
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(next.Definition, next.Layout); error = null; message = null;
    }

    private void ChangedLayout(WorkflowLayout next)
    {
        if (definition is null || layout is null || WorkflowLayoutJson.ComputeHash(next, definition) == layoutHash) return;
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(definition, next); error = null; message = null;
    }

    private void Undo() { if (undo.TryPop(out var previous)) { redo.Push(new(definition!, layout!)); SetDesign(previous.Definition, previous.Layout); message = null; } }
    private void Redo() { if (redo.TryPop(out var next)) { undo.Push(new(definition!, layout!)); SetDesign(next.Definition, next.Layout); message = null; } }

    private Task SelectWorkflow(ChangeEventArgs args) => Perform(async current =>
    {
        if (dirty) return;
        var draft = await current.Catalog.GetDraftAsync(args.Value?.ToString() ?? "").WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        await Load(draft, current);
    });

    private void SelectRevision(ChangeEventArgs args)
    { selectedRevisionId = Guid.TryParse(args.Value?.ToString(), out var id) ? id : null; preview = null; }

    private Task Save() => Perform(async current =>
    {
        var draft = current.Catalog is IWorkflowDesignCatalog designs
            ? (await designs.SaveDesignAsync(definition!, layout!, generation).WaitAsync(current.Token)).Draft
            : await current.Catalog.SaveDraftAsync(definition!, generation).WaitAsync(current.Token);
        await Load(draft, current, resetHistory: false);
        if (Current(current)) message = "Draft saved.";
    });

    private Task Publish() => Perform(async current =>
    {
        if (dirty || generation == 0) throw new InvalidOperationException();
        var id = definition!.WorkflowId;
        var published = await current.Catalog.PublishAsync(id, generation).WaitAsync(current.Token);
        var draft = await current.Catalog.GetDraftAsync(id).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
        await Load(draft, current);
        if (!Current(current)) return;
        selectedRevisionId = published.Revision.RevisionId;
        message = $"Published and activated version {published.Version}.";
    });

    private Task Discard() => Perform(async current =>
    {
        var draft = definition is null ? null : await current.Catalog.GetDraftAsync(definition.WorkflowId).WaitAsync(current.Token);
        if (!Current(current)) return;
        if (draft is null) NewWorkflow(); else await Load(draft, current);
        if (Current(current)) message = "Local changes discarded; saved state loaded.";
    });

    private Task Preview() => Perform(async current =>
    {
        var selected = versions.FirstOrDefault(v => v.Revision.RevisionId == selectedRevisionId);
        var presentation = selected is not null && current.Catalog is IWorkflowDesignCatalog designs
            ? await designs.GetRevisionLayoutAsync(selected.Revision.RevisionId).WaitAsync(current.Token) : null;
        if (Current(current)) { preview = selected; previewLayout = presentation; }
    });
    private Task Restore() => Perform(async current =>
    {
        if (dirty) throw new InvalidOperationException();
        var draft = await current.Catalog.RestoreDraftAsync(definition!.WorkflowId, selectedRevisionId!.Value, generation).WaitAsync(current.Token);
        await Load(draft, current);
        if (Current(current)) message = "Version restored to draft. Publish to create a new version; active runs are unchanged.";
    });

    private Task Activate() => Perform(async current =>
    {
        if (dirty) throw new InvalidOperationException();
        var draft = await current.Catalog.ActivateAsync(definition!.WorkflowId, selectedRevisionId!.Value, generation).WaitAsync(current.Token);
        var metadata = await ReadMetadata(current, draft.Definition.WorkflowId);
        if (!Current(current)) return;
        generation = draft.Generation; activeRevisionId = draft.ActiveRevisionId;
        ApplyMetadata(metadata); message = "Selected version activated for new workflow runs.";
    });

    private Task RunPublished() => Perform(async current =>
    {
        var request = startPublishedPaused && current.Client is IPublishedWorkflowRunControl controls
            ? controls.StartPublishedPausedRunAsync(selectedRevisionId!.Value, runInput)
            : current.Client.StartPublishedRunAsync(selectedRevisionId!.Value, runInput);
        var run = await request.WaitAsync(current.Token);
        if (!Current(current)) return;
        await OnRunStarted.InvokeAsync(run);
        if (Current(current)) message = startPublishedPaused
            ? "Published run created paused. Resume it from the run controls."
            : "Published run started independently of the editor.";
    });

    private void Export() { jsonText = WorkflowJson.Serialize(preview?.Revision.Definition ?? definition!); }
    private void ExportLayout() => jsonText = WorkflowLayoutJson.Serialize(
        preview is not null ? previewLayout ?? WorkflowLayout.Create(preview.Revision.Definition) : layout!, preview?.Revision.Definition ?? definition!);
    private Task ImportLayout() => Perform(_ =>
    {
        if (preview is not null || Catalog is not IWorkflowDesignCatalog) return Task.CompletedTask;
        ChangedLayout(WorkflowLayoutJson.Deserialize(jsonText, definition!));
        message = "Graph layout imported locally. Save and publish explicitly.";
        return Task.CompletedTask;
    });

    private Task RefreshTools() => Perform(async current =>
    {
        await Choices(current);
        if (Current(current) && error is null) message = "MCP tool catalog refreshed. Existing contracts remain pinned.";
    });

    private Task Import() => Perform(_ =>
    {
        var imported = WorkflowJson.Deserialize(jsonText) with { WorkflowId = definition!.WorkflowId };
        Changed(imported); message = "Definition imported locally. Save and publish explicitly.";
        return Task.CompletedTask;
    });

    private async Task Perform(Func<Binding, Task> action)
    {
        var current = binding;
        if (busy || current is null || !Current(current)) return;
        busy = true; error = null; message = null;
        try { await action(current); }
        catch (OperationCanceledException) when (current.Token.IsCancellationRequested) { }
        catch (WorkflowConflictException)
        { if (Current(current)) error = "Another editor changed this workflow. Your local edits are preserved. Discard local changes and reload before continuing."; }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        { if (Current(current)) error = "Invalid workflow, layout, or JSON. Check the supported pipeline and graph limits."; }
        catch
        { if (Current(current)) error = "The operation failed. Local edits are preserved; reload saved state before retrying."; }
        finally { if (Current(current)) busy = false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        choicesLifetime?.Dispose();
        contextLifetime?.Dispose();
        lifetime.Dispose();
    }
}
