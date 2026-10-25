using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class GraphCanvasRenderingTests
{
    [Fact]
    public async Task RecordedLayoutEncodesUntrustedLabelsAndCommentsWithoutExecutionServices()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(600, 250), "<img src=x onerror=alert(1)>", "<script>comment</script>")),
            Sections = layout.Sections.Add("section", new("<script>section</script>", ["step-1"]))
        };
        var revision = WorkflowRevision.Create(definition, "test");
        var snapshot = new RunSnapshot(new(Guid.NewGuid(), revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "hello"), []);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowRunViewer.Snapshot)] = snapshot, [nameof(WorkflowRunViewer.Layout)] = layout }));
            return component.ToHtmlString();
        });
        Assert.Contains("&lt;img", html);
        Assert.Contains("&lt;script&gt;comment&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;section&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("left:600px;top:250px", html);
        Assert.Contains("data-disabled=\"true\"", html);
    }
}
