using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class GraphFunctionExtractionDialog
{
    [Inject] private IJSRuntime JavaScript { get; set; } = default!;
    [Parameter] public bool Open { get; set; }
    [Parameter] public GraphFunctionExtractionPlan? Plan { get; set; }
    [Parameter] public string Name { get; set; } = "";
    [Parameter] public string Description { get; set; } = "";
    [Parameter] public string? PreparationError { get; set; }
    [Parameter] public string? OperationError { get; set; }
    [Parameter] public bool OperationBusy { get; set; }
    [Parameter] public EventCallback<string> NameChanged { get; set; }
    [Parameter] public EventCallback<string> DescriptionChanged { get; set; }
    [Parameter] public EventCallback Apply { get; set; }
    [Parameter] public EventCallback Cancel { get; set; }

    private ElementReference dialog;
    private bool dialogShown;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !dialogShown)
        {
            var assetVersion = typeof(GraphFunctionExtractionDialog).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
            await module.InvokeVoidAsync("showModalDialog", dialog);
            dialogShown = true;
        }
        else if (!Open && dialogShown)
        {
            var assetVersion = typeof(GraphFunctionExtractionDialog).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/tabInteractions.js?v={assetVersion}");
            await module.InvokeVoidAsync("closeModalDialog", dialog);
            dialogShown = false;
        }
    }

    private Task Closed() => Cancel.InvokeAsync();
}
