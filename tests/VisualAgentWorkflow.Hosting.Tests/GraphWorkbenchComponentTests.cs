using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class GraphWorkbenchComponentTests
{
    [Fact]
    public async Task CanvasKeepsOptionalInspectorSelectionValidWhenDefinitionChanges()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var definition = WorkflowTemplates.Create("uppercase");
        WorkflowGraphCanvas? canvas = null;
        RenderFragment<string> summary = id => builder => builder.AddContent(0, $"Summary {id}");
        RenderFragment<string> inspector = id => builder => builder.AddContent(0, $"Properties {id}");
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowGraphCanvas>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowGraphCanvas.Definition)] = definition,
                    [nameof(WorkflowGraphCanvas.Layout)] = WorkflowLayout.Create(definition),
                    [nameof(WorkflowGraphCanvas.NodeContent)] = summary,
                    [nameof(WorkflowGraphCanvas.NodeInspector)] = inspector,
                    [nameof(WorkflowGraphCanvas.SelectedNodeId)] = "step-1"
                },
                ["Capture"] = (Action<WorkflowGraphCanvas>)(component => canvas = component)
            })));
        await root.QuiescenceTask;
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Properties step-1", html);
        Assert.DoesNotContain("Properties input", html);
        Assert.Contains("Summary input", html);
        Assert.Contains("Summary output", html);

        var removed = WorkflowEditing.Remove(definition, "step-1");
        await renderer.Dispatcher.InvokeAsync(() => canvas!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(WorkflowGraphCanvas.Definition)] = removed,
            [nameof(WorkflowGraphCanvas.Layout)] = WorkflowLayout.Create(removed)
        })));
        html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Properties input", html);
        Assert.DoesNotContain("Properties step-1", html);
        // Rendering and disposal require no JS runtime, workflow client or execution services.
    }
}
