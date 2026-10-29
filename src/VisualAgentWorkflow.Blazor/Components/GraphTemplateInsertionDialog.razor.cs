using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public sealed record GraphTemplatePortOption(
    string Value,
    string Label,
    bool ReplacesExistingConnection = false);

public sealed record GraphTemplateBindingChange(string TemplatePortId, string Value, bool IsInput);

public partial class GraphTemplateInsertionDialog
{
    [Inject] private IJSRuntime JavaScript { get; set; } = default!;
    [Parameter] public bool Open { get; set; }
    [Parameter, EditorRequired] public GraphTemplateDefinition Template { get; set; } = default!;
    [Parameter] public IReadOnlyList<GraphTemplatePortOption> InputOptions { get; set; } = [];
    [Parameter] public IReadOnlyList<GraphTemplatePortOption> OutputOptions { get; set; } = [];
    [Parameter] public IReadOnlyDictionary<string, string> InputBindings { get; set; } = new Dictionary<string, string>();
    [Parameter] public IReadOnlyDictionary<string, string> OutputBindings { get; set; } = new Dictionary<string, string>();
    [Parameter] public string? PreviewMessage { get; set; }
    [Parameter] public int PreviewNodeCount { get; set; }
    [Parameter] public int PreviewConnectionCount { get; set; }
    [Parameter] public int PreviewReplacementCount { get; set; }
    [Parameter] public EventCallback<GraphTemplateBindingChange> BindingChanged { get; set; }
    [Parameter] public EventCallback Apply { get; set; }
    [Parameter] public EventCallback Cancel { get; set; }

    private ElementReference dialog;
    private bool dialogShown;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !dialogShown)
        {
            var assetVersion = typeof(GraphTemplateInsertionDialog).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
            await module.InvokeVoidAsync("showModalDialog", dialog);
            dialogShown = true;
        }
        else if (!Open && dialogShown)
        {
            var assetVersion = typeof(GraphTemplateInsertionDialog).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
            await module.InvokeVoidAsync("closeModalDialog", dialog);
            dialogShown = false;
        }
    }

    private Task Changed(string portId, bool isInput, ChangeEventArgs args) =>
        BindingChanged.InvokeAsync(new GraphTemplateBindingChange(portId, args.Value?.ToString() ?? "", isInput));

    private Task Closed() => Cancel.InvokeAsync();
}
