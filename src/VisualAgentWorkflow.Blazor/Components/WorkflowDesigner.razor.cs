using Microsoft.AspNetCore.Components.Web;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowDesigner
{
    [Inject] private IServiceProvider Services { get; set; } = default!;
    private string workflowFilter = "";
    [Parameter, EditorRequired] public IWorkflowCatalog Catalog { get; set; } = default!;
    [Parameter, EditorRequired] public IPublishedWorkflowClient Client { get; set; } = default!;
    [Parameter] public EventCallback<RunSnapshot> OnRunStarted { get; set; }
    [Parameter] public EventCallback OpenRunInspector { get; set; }
    [Parameter] public IToolBindingCatalog? ToolCatalog { get; set; }
    [Parameter] public IAgentProviderCatalog? AgentCatalog { get; set; }
    [Parameter] public ISkillLibrary? SkillLibrary { get; set; }
    [Parameter] public IAgentProfileLibrary? AgentProfileLibrary { get; set; }
    [Parameter] public IGraphFunctionCatalog? FunctionCatalog { get; set; }
    [Parameter] public IGraphTemplateCatalog? TemplateCatalog { get; set; }
    [Parameter] public string ProjectId { get; set; } = "default";
    [Parameter] public bool IdeMode { get; set; }
    [Parameter] public string? InitialWorkflowId { get; set; }
    [Parameter] public Func<Task>? DraftSaved { get; set; }
    [Parameter] public EventCallback<string> WorkflowSelected { get; set; }
    private string? notifiedWorkflowId;

    private sealed record Binding(IWorkflowCatalog Catalog, IPublishedWorkflowClient Client, CancellationToken Token);
    private Binding? binding;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? contextLifetime;
    private CancellationTokenSource? choicesLifetime;
    private IToolBindingCatalog? loadedTools;
    private IAgentProviderCatalog? loadedAgents;
    private IToolBindingCatalog? requestedTools;
    private IAgentProviderCatalog? requestedAgents;
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
    private string bottomTab = "runs";
    private bool bottomTrayExpanded = true;
    private bool runValidationRequested;
    private bool documentSettingsOpen;
    private readonly List<string> openWorkflowIds = [];
    private string? draggedWorkflowTabId;
    private string? workflowDropTargetId;
    private ElementReference workflowTabStrip;
    private ElementReference designerRoot;
    private IJSObjectReference? paneModule;
    private IJSObjectReference? paneSession;
    [Parameter] public RenderFragment? RunHistoryContent { get; set; }
    [Parameter] public RenderFragment? OutputContent { get; set; }
    private Task RequestRunInspector() => OpenRunInspector.InvokeAsync();
    private bool runDialogOpen;
    private bool runDialogShown;
    private ElementReference runDialog;

    private void OpenRunDialog()
    {
        if (!IdeMode || busy || dirty || generation == 0) return;
        if (!CurrentDraftIsValid)
        {
            runValidationRequested = true;
            SelectBottomTab("issues");
            return;
        }
        error = null;
        message = null;
        runDialogOpen = true;
    }

    private async Task CloseRunDialog()
    {
        if (runDialogShown && Services.GetService(typeof(IJSRuntime)) is IJSRuntime js)
        {
            try
            {
                var assetVersion = typeof(WorkflowDesigner).Assembly.ManifestModule.ModuleVersionId.ToString("N");
                await using var module = await js.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
                await module.InvokeVoidAsync("closeModalDialog", runDialog);
            }
            catch (JSException) { }
        }
        runDialogShown = false;
        runDialogOpen = false;
    }

    private void SelectBottomTab(string tab)
    {
        bottomTab = tab;
        bottomTrayExpanded = true;
    }
    private bool loadingWorkflow = true;
    private bool mcpChoicesLoading;
    private bool providerChoicesLoading;
    private bool choicesLoading => mcpChoicesLoading || providerChoicesLoading;
    private string? mcpChoicesError;
    private string? providerChoicesError;
    private string? error;
    private string? message;
    private string jsonText = "";
    private string runInput = "Hello, workflows!";
    private readonly Dictionary<string, string> runParameters = new(StringComparer.Ordinal);
    private bool startPublishedPaused;
    private WorkflowDefinition? downloadedDefinition;
    private string downloadHref = "";
    private WorkflowDefinition? ViewedDefinition => preview?.Revision.Definition ?? definition;
    private bool CurrentDraftIsValid => definition is { } current && WorkflowValidator.Validate(current).IsEmpty;
    private bool CanExportViewedDefinition => ViewedDefinition is { } viewed && WorkflowValidator.Validate(viewed).IsEmpty;

    private string DownloadHref
    {
        get
        {
            var viewed = ViewedDefinition;
            if (viewed is not null && !ReferenceEquals(viewed, downloadedDefinition))
            {
                downloadHref = CanExportViewedDefinition
                    ? "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkflowJson.Serialize(viewed)))
                    : "";
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
            var viewed = ViewedDefinition;
            var presentation = preview is not null ? previewLayout ?? WorkflowLayout.Create(preview.Revision.Definition) : layout;
            if (viewed is not null && presentation is not null &&
                (!ReferenceEquals(viewed, layoutDownloadDefinition) || !ReferenceEquals(presentation, downloadedLayout)))
            {
                layoutDownloadHref = CanExportViewedDefinition
                    ? "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(WorkflowLayoutJson.Serialize(presentation, viewed)))
                    : "";
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
            {
                if (ReferenceEquals(requestedTools, ToolCatalog) && ReferenceEquals(requestedAgents, AgentCatalog))
                    return;
                mcpChoicesLoading = ToolCatalog is not null;
                providerChoicesLoading = AgentCatalog is not null;
                mcpChoicesError = null; providerChoicesError = null;
                if (interactive) _ = LoadChoicesAndRenderAsync(binding);
            }
            return;
        }

        var previous = contextLifetime;
        contextLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        binding = new(Catalog, Client, contextLifetime.Token);
        previous?.Cancel();
        previous?.Dispose();
        bottomTab = IdeMode ? "runs" : "run";
        openWorkflowIds.Clear();
        definition = null; layout = null; previewLayout = null; layoutHash = null; definitionHash = null; savedHash = null;
        runValidationRequested = false;
        workflows = []; versions = []; audit = []; bindings = []; providers = [];
        loadingWorkflow = true;
        mcpChoicesLoading = ToolCatalog is not null;
        providerChoicesLoading = AgentCatalog is not null;
        mcpChoicesError = null; providerChoicesError = null;
        preview = null; selectedRevisionId = null; activeRevisionId = null;
        undo.Clear(); redo.Clear(); jsonText = ""; runInput = "Hello, workflows!"; runParameters.Clear(); startPublishedPaused = false; busy = false;
        loadedTools = null; loadedAgents = null; requestedTools = null; requestedAgents = null; ResetReuse();
        await Perform(async current =>
        {
            var drafts = await current.Catalog.ListDraftsAsync().WaitAsync(current.Token);
            if (!Current(current)) return;
            var initial = drafts.FirstOrDefault(draft => draft.Definition.WorkflowId == InitialWorkflowId);
            if (initial is not null) await Load(initial, current);
            else if (drafts.Count > 0) await Load(drafts[0], current);
            else NewWorkflow();
            if (Current(current))
            {
                loadingWorkflow = false;
                if (interactive) _ = LoadChoicesAndRenderAsync(current);
            }
        });
        if (Current(binding)) loadingWorkflow = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !disposed && Services.GetService(typeof(IJSRuntime)) is IJSRuntime layoutJs)
        {
            var assetVersion = typeof(WorkflowDesigner).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            var layoutModule = await layoutJs.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/paneLayout.js?v={assetVersion}");
            paneModule = layoutModule;
            paneSession = await layoutModule.InvokeAsync<IJSObjectReference>("attach", designerRoot);
        }
        if (IdeMode && definition is not null && Services.GetService(typeof(IJSRuntime)) is IJSRuntime js)
            await js.InvokeVoidAsync("vawStudioIde.revealActiveTab", workflowTabStrip);
        if (runDialogOpen && !runDialogShown && Services.GetService(typeof(IJSRuntime)) is IJSRuntime dialogJs)
        {
            var assetVersion = typeof(WorkflowDesigner).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            await using var module = await dialogJs.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
            await module.InvokeVoidAsync("showModalDialog", runDialog);
            runDialogShown = true;
        }
        if (firstRender && !disposed)
        {
            interactive = true;
            if (binding is not null && choicesLoading) _ = LoadChoicesAndRenderAsync(binding);
            StateHasChanged();
        }
        if (!disposed && definition is { } current && notifiedWorkflowId != current.WorkflowId)
        {
            notifiedWorkflowId = current.WorkflowId;
            await WorkflowSelected.InvokeAsync(current.WorkflowId);
        }
    }

    private bool Current(Binding current) => !disposed && ReferenceEquals(binding, current);

    private void NewWorkflow()
    {
        ResetReuse();
        var next = WorkflowEditing.Remove(WorkflowTemplates.Create("uppercase"), "step-1") with
        {
            WorkflowId = "workflow-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Untitled workflow"
        };
        SetDesign(next, WorkflowLayout.Create(next));
        generation = 0; activeRevisionId = null; selectedRevisionId = null; preview = null;
        versions = []; audit = []; undo.Clear(); redo.Clear(); jsonText = "";
        savedHash = DesignHash;
        error = null; message = IdeMode ? "New workflow created. Name it in Workflow settings, then save." : "New workflow created locally. Save its draft before publishing.";
    }

    private void RenameWorkflow(ChangeEventArgs args)
    {
        if (definition is null) return;
        try
        {
            Changed(WorkflowMetadataEditing.Rename(definition, args.Value?.ToString() ?? ""));
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
        }
    }

    private static string WorkflowDisplayName(WorkflowDefinition workflow) =>
        string.IsNullOrWhiteSpace(workflow.DisplayName) ? workflow.WorkflowId : workflow.DisplayName;

    private bool WorkflowMatchesFilter(WorkflowDefinition workflow) =>
        WorkflowDisplayName(workflow).Contains(workflowFilter, StringComparison.OrdinalIgnoreCase) ||
        workflow.WorkflowId.Contains(workflowFilter, StringComparison.OrdinalIgnoreCase);

    private void SetDesign(WorkflowDefinition value, WorkflowLayout presentation)
    {
        ClearUpgradeReview();
        definition = value; layout = presentation;
        openWorkflowIds.RemoveAll(id => id != value.WorkflowId && !workflows.Any(item => item.Definition.WorkflowId == id));
        if (!openWorkflowIds.Contains(value.WorkflowId)) openWorkflowIds.Add(value.WorkflowId);
        definitionHash = WorkflowJson.ComputeDraftHash(value);
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
        requestedTools = tools;
        requestedAgents = agents;
        var version = ++choicesVersion;
        choicesLifetime?.Cancel();
        choicesLifetime?.Dispose();
        choicesLifetime = CancellationTokenSource.CreateLinkedTokenSource(current.Token);
        var token = choicesLifetime.Token;
        mcpChoicesLoading = tools is not null;
        providerChoicesLoading = agents is not null;
        mcpChoicesError = null; providerChoicesError = null;
        var bindingsTask = tools is null
            ? Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([])
            : StartChoiceLoad(() => tools.ListBindingsAsync(token).WaitAsync(token));
        var providersTask = agents is null
            ? Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([])
            : StartChoiceLoad(() => agents.ListProvidersAsync().WaitAsync(token));
        await Task.WhenAll(
            LoadToolChoicesAsync(bindingsTask, tools, current, version, token),
            LoadProviderChoicesAsync(providersTask, agents, current, version, token));
    }

    private async Task LoadToolChoicesAsync(Task<IReadOnlyList<ToolBindingDescriptor>> pending,
        IToolBindingCatalog? catalog, Binding current, long version, CancellationToken token)
    {
        try
        {
            var result = await pending;
            if (!Current(current) || version != choicesVersion) return;
            bindings = result; loadedTools = catalog;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(current) && version == choicesVersion)
                mcpChoicesError = "Could not load MCP tools. The workflow remains editable; use Refresh MCP tools to retry.";
        }
        finally
        {
            if (Current(current) && version == choicesVersion) mcpChoicesLoading = false;
            await RenderChoiceStatusAsync();
        }
    }

    private async Task LoadProviderChoicesAsync(Task<IReadOnlyList<AgentProviderDescriptor>> pending,
        IAgentProviderCatalog? catalog, Binding current, long version, CancellationToken token)
    {
        try
        {
            var result = await pending;
            if (!Current(current) || version != choicesVersion) return;
            providers = result; loadedAgents = catalog;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(current) && version == choicesVersion)
                providerChoicesError = "Could not load model connections. Check Studio settings and retry the provider list.";
        }
        finally
        {
            if (Current(current) && version == choicesVersion) providerChoicesLoading = false;
            await RenderChoiceStatusAsync();
        }
    }

    private static Task<T> StartChoiceLoad<T>(Func<Task<T>> load)
    {
        try { return load(); }
        catch (Exception exception) { return Task.FromException<T>(exception); }
    }

    private async Task RenderChoiceStatusAsync()
    {
        if (disposed) return;
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { }
    }

    private async Task LoadChoicesAndRenderAsync(Binding current)
    {
        await Choices(current);
        if (!Current(current)) return;
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { }
    }

    private void CloseSettingsOnEscape(KeyboardEventArgs args) { if (args.Key == "Escape") documentSettingsOpen = false; }

    private void Changed(WorkflowDefinition next) => ChangeDefinition(next, resetLayout: false);
    private void ChangedImported(WorkflowDefinition next) => ChangeDefinition(next, resetLayout: true);

    private void ChangeDefinition(WorkflowDefinition next, bool resetLayout)
    {
        if (definition is null || layout is null || WorkflowJson.ComputeDraftHash(next) == definitionHash) return;
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(next, resetLayout ? WorkflowLayout.Create(next) : layout.Reconcile(next)); error = null; message = null; runValidationRequested = false;
    }

    private void ChangedGraph(WorkflowGraphEdit next)
    {
        if (definition is null || layout is null) return;
        if (WorkflowJson.ComputeDraftHash(next.Definition) == definitionHash && WorkflowLayoutJson.ComputeHash(next.Layout, next.Definition) == layoutHash) return;
        WorkflowLayoutJson.Validate(next.Layout, next.Definition);
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(next.Definition, next.Layout); error = null; message = null; runValidationRequested = false;
    }

    private void ChangedLayout(WorkflowLayout next)
    {
        if (definition is null || layout is null || WorkflowLayoutJson.ComputeHash(next, definition) == layoutHash) return;
        undo.Push(new(definition, layout)); redo.Clear();
        SetDesign(definition, next); error = null; message = null; runValidationRequested = false;
    }

    private void Undo() { if (undo.TryPop(out var previous)) { redo.Push(new(definition!, layout!)); SetDesign(previous.Definition, previous.Layout); message = null; } }
    private void Redo() { if (redo.TryPop(out var next)) { undo.Push(new(definition!, layout!)); SetDesign(next.Definition, next.Layout); message = null; } }

    private string? pendingCloseWorkflowId;

    private void StartWorkflowTabDrag(string id) => draggedWorkflowTabId = id;
    private void ClearWorkflowTabDrag() { draggedWorkflowTabId = null; workflowDropTargetId = null; }
    private void DropWorkflowTab(string targetId)
    {
        if (draggedWorkflowTabId is { } sourceId)
        {
            var sourceIndex = openWorkflowIds.IndexOf(sourceId);
            var targetIndex = openWorkflowIds.IndexOf(targetId);
            if (sourceIndex >= 0 && targetIndex >= 0 && sourceIndex != targetIndex)
            {
                openWorkflowIds.RemoveAt(sourceIndex);
                openWorkflowIds.Insert(targetIndex, sourceId);
            }
        }
        ClearWorkflowTabDrag();
    }
    private void ReorderWorkflowTabFromKeyboard(string id, KeyboardEventArgs args)
    {
        if (!args.AltKey || !args.ShiftKey || args.Key is not ("ArrowLeft" or "ArrowRight")) return;
        var sourceIndex = openWorkflowIds.IndexOf(id);
        var targetIndex = sourceIndex + (args.Key == "ArrowLeft" ? -1 : 1);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= openWorkflowIds.Count) return;
        openWorkflowIds.RemoveAt(sourceIndex);
        openWorkflowIds.Insert(targetIndex, id);
    }

    private Task CloseWorkflowMiddle(string id, Microsoft.AspNetCore.Components.Web.MouseEventArgs args) =>
        args.Button == 1 ? RequestCloseWorkflow(id) : Task.CompletedTask;

    private Task RequestCloseWorkflow(string id)
    {
        if (busy || pendingCloseWorkflowId is not null || !openWorkflowIds.Contains(id)) return Task.CompletedTask;
        if (definition?.WorkflowId == id && (dirty || generation == 0))
        {
            pendingCloseWorkflowId = id;
            return Task.CompletedTask;
        }
        return CloseWorkflow(id);
    }

    private void CancelCloseWorkflow() => pendingCloseWorkflowId = null;

    private Task ConfirmCloseWorkflow()
    {
        var id = pendingCloseWorkflowId;
        pendingCloseWorkflowId = null;
        return id is null ? Task.CompletedTask : CloseWorkflow(id);
    }

    private Task CloseWorkflow(string id) => Perform(async current =>
    {
        var index = openWorkflowIds.IndexOf(id);
        if (index < 0) return;
        if (definition?.WorkflowId != id)
        {
            openWorkflowIds.RemoveAt(index);
            return;
        }
        var nextId = openWorkflowIds.Count > 1 ? openWorkflowIds[index + 1 < openWorkflowIds.Count ? index + 1 : index - 1] : null;
        if (nextId is not null)
        {
            var draft = await current.Catalog.GetDraftAsync(nextId).WaitAsync(current.Token) ?? throw new KeyNotFoundException();
            await Load(draft, current);
            if (Current(current)) openWorkflowIds.Remove(id);
            return;
        }
        openWorkflowIds.Remove(id);
        definition = null; layout = null; preview = null; previewLayout = null;
        definitionHash = null; layoutHash = null; savedHash = null;
        undo.Clear(); redo.Clear(); versions = []; audit = []; jsonText = "";
        generation = 0; activeRevisionId = null; selectedRevisionId = null; notifiedWorkflowId = null;
        ResetReuse();
    });

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
        if (Current(current) && DraftSaved is not null) await DraftSaved();
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

    private bool CanPromoteWorkflowCallDraft => !IdeMode && !dirty && !busy && generation > 0 &&
        Catalog is IWorkflowDraftPromotionCatalog && definition is not null && layout is not null &&
        (definition.SchemaVersion is WorkflowSchemaVersions.SequenceTransformChainGraph or WorkflowSchemaVersions.SequenceWorkflowCallGraph) &&
        definition.Nodes.Values.Any(node => node.Type == NodeTypes.WorkflowCall);

    private Task PromoteWorkflowCallDraft() => Perform(async current =>
    {
        if (!CanPromoteWorkflowCallDraft || definition is null || layout is null ||
            current.Catalog is not IWorkflowDraftPromotionCatalog promotions) return;
        var draft = await current.Catalog.GetDraftAsync(definition.WorkflowId).WaitAsync(current.Token)
            ?? throw new KeyNotFoundException("Workflow draft not found.");
        var candidate = WorkflowDraftPromotionCandidate.Create(definition, layout, generation);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "studio", draft.HeadRevisionId);
        var published = await promotions.PublishPromotedDraftAsync(promotion, generation).WaitAsync(current.Token);
        var refreshed = await current.Catalog.GetDraftAsync(definition.WorkflowId).WaitAsync(current.Token)
            ?? throw new KeyNotFoundException("Promoted workflow draft not found.");
        await Load(refreshed, current);
        if (Current(current))
        {
            selectedRevisionId = published.Revision.RevisionId;
            message = $"Promoted the call-bearing draft to schema {published.Revision.Definition.SchemaVersion} and published version {published.Version}.";
        }
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
        if (IdeMode && dirty)
            throw new InvalidOperationException("Save the workflow before starting a run.");
        if (IdeMode && WorkflowValidator.Validate(definition!).Length > 0)
            throw new InvalidOperationException("Resolve workflow issues before starting a run.");

        var parameterOverrides = SelectedRunParameters.Count == 0 ? null : SelectedRunParameters.Keys
            .ToDictionary(id => id, id => RunParameterValue(id, SelectedRunParameters[id]), StringComparer.Ordinal);
        Task<RunSnapshot> request;
        if (IdeMode)
        {
            if (parameterOverrides is not null)
            {
                if (current.Client is not IParameterizedWorkflowClient parameterized)
                    throw new InvalidOperationException("The configured run client does not support workflow parameters.");
                request = startPublishedPaused
                    ? parameterized.StartPausedRunWithParametersAsync(definition!, runInput, parameterOverrides)
                    : parameterized.StartRunWithParametersAsync(definition!, runInput, parameterOverrides);
            }
            else
            {
                request = startPublishedPaused && current.Client is IWorkflowRunControl controls
                    ? controls.StartPausedRunAsync(definition!, runInput)
                    : current.Client.StartRunAsync(definition!, runInput);
            }
        }
        else if (parameterOverrides is not null)
        {
            if (current.Client is not IParameterizedPublishedWorkflowClient parameterized)
                throw new InvalidOperationException("The configured run client does not support workflow parameters.");
            request = startPublishedPaused
                ? parameterized.StartPublishedPausedWithParametersAsync(selectedRevisionId!.Value, runInput, parameterOverrides)
                : parameterized.StartPublishedRunWithParametersAsync(selectedRevisionId!.Value, runInput, parameterOverrides);
        }
        else
        {
            request = startPublishedPaused && current.Client is IPublishedWorkflowRunControl controls
                ? controls.StartPublishedPausedRunAsync(selectedRevisionId!.Value, runInput)
                : current.Client.StartPublishedRunAsync(selectedRevisionId!.Value, runInput);
        }
        var run = await request.WaitAsync(current.Token);
        if (!Current(current)) return;
        await OnRunStarted.InvokeAsync(run);
        if (IdeMode)
        {
            bottomTab = "runs";
            bottomTrayExpanded = true;
            await CloseRunDialog();
        }
        if (Current(current)) message = startPublishedPaused
            ? "Workflow run created paused. Resume it from the run controls."
            : IdeMode ? "Workflow run started. You can keep editing while it runs."
            : "Published run started independently of the editor.";
    });

    private System.Collections.Immutable.ImmutableSortedDictionary<string, WorkflowParameterDefinition> SelectedRunParameters =>
        IdeMode ? definition?.Parameters ?? System.Collections.Immutable.ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty :
        selectedRevisionId is { } revisionId
            ? versions.FirstOrDefault(version => version.Revision.RevisionId == revisionId)?.Revision.Definition.Parameters ??
                System.Collections.Immutable.ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            : System.Collections.Immutable.ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;

    private string RunParameterValue(string id, WorkflowParameterDefinition parameter) =>
        runParameters.TryGetValue(id, out var value) ? value : parameter.DefaultValue;

    private void SetRunParameter(string id, ChangeEventArgs args) => runParameters[id] = args.Value?.ToString() ?? "";

    private void Export() { if (CanExportViewedDefinition) jsonText = WorkflowJson.Serialize(ViewedDefinition!); }
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
        if (Current(current) && mcpChoicesError is null) message = "MCP tool catalog refreshed. Existing contracts remain pinned.";
    });

    private Task Import() => Perform(_ =>
    {
        var imported = WorkflowJson.Deserialize(jsonText) with { WorkflowId = definition!.WorkflowId };
        ChangedImported(imported); message = "Definition imported locally. Save and publish explicitly.";
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
        { if (Current(current)) error = "Invalid workflow, layout, or JSON. Check the supported workflow and graph limits."; }
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
        if (paneSession is not null)
        {
            try { await paneSession.InvokeVoidAsync("dispose"); await paneSession.DisposeAsync(); if (paneModule is not null) await paneModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
