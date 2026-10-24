using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ApprovalRenderingTests
{
    private static RunSnapshot Waiting()
    {
        var graph = GraphEditing.InsertApprovalBefore(PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64)))), "step-1");
        var approval = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Approval).Key;
        var revision = WorkflowRevision.Create(graph, "fixture");
        var id = Guid.NewGuid();
        var invocation = Guid.NewGuid();
        var action = new WorkflowApprovalAction(id, revision.RevisionId, revision.ContentHash, approval, invocation,
            "step-1", Guid.NewGuid(), graph.Nodes["step-1"].Tool!, "<script>input</script>", WorkflowApprovalAction.HashInput("<script>input</script>"));
        var request = new WorkflowApprovalRequest(Guid.NewGuid(), action, action.ComputeHash(), "<script>review</script>", DateTimeOffset.UtcNow);
        return new(new(id, revision.RevisionId, null, "fixture", revision.CreatedAt, revision.ContentHash, graph, "input"),
            [new(id, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.ApprovalRequested, approval, invocation,
                Data: JsonSerializer.Serialize(request, JsonSerializerOptions.Web))]);
    }

    [Fact]
    public async Task ReadOnlyViewerDisplaysExactEvidenceWithoutExecutionOrControlServices()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowRunViewer.Snapshot)] = Waiting() }));
            return root.ToHtmlString();
        });
        Assert.Contains("Awaiting approval", html);
        Assert.Contains("&lt;script&gt;review&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;input&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("Approve recorded action", html);
        Assert.DoesNotContain("Reject recorded action", html);
    }

    [Fact]
    public async Task RecordedPositionCannotExposeDecisionControlsEvenWhenClientExists()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var client = new NeverDecide();
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<WorkflowApprovalPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowApprovalPanel.Snapshot)] = Waiting(), [nameof(WorkflowApprovalPanel.Client)] = client, [nameof(WorkflowApprovalPanel.CanDecide)] = false }));
            return root.ToHtmlString();
        });
        Assert.Contains("Read-only approval evidence", html);
        Assert.DoesNotContain("Approve recorded action", html);
    }

    private sealed class NeverDecide : IWorkflowApprovalClient
    {
        public Task DecideApprovalAsync(Guid runId, Guid requestId, string actionHash, bool approve) => throw new InvalidOperationException("Rendering cannot decide an action.");
    }
}
