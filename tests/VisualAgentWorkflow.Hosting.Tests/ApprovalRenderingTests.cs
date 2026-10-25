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
        graph = graph with { Nodes = graph.Nodes.SetItem(approval, graph.Nodes[approval] with { Approval = new("<script>review</script>") }) };
        var revision = WorkflowRevision.Create(graph, "fixture");
        var id = Guid.NewGuid();
        var invocation = Guid.NewGuid();
        var action = new WorkflowApprovalAction(id, revision.RevisionId, revision.ContentHash, approval, invocation,
            "step-1", Guid.NewGuid(), graph.Nodes["step-1"].Tool!, "<script>input</script>", WorkflowApprovalAction.HashInput("<script>input</script>"));
        Assert.True(WorkflowApprovalEvidence.MatchesDefinition(graph, id, revision.RevisionId, revision.ContentHash, action, graph.Nodes[approval].Approval!.Prompt));
        var request = new WorkflowApprovalRequest(Guid.NewGuid(), action, action.ComputeHash(), graph.Nodes[approval].Approval!.Prompt, DateTimeOffset.UtcNow);
        var snapshot = new RunSnapshot(new(id, revision.RevisionId, null, "fixture", revision.CreatedAt, revision.ContentHash, graph, "input"),
            [new(id, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.ApprovalRequested, approval, invocation,
                Data: JsonSerializer.Serialize(request, JsonSerializerOptions.Web))]);
        var recorded = WorkflowApprovalEvidence.ReadRequest(snapshot.Events[0]);
        Assert.NotNull(recorded);
        Assert.Equal("waiting-approval", snapshot.Status);
        Assert.Equal(recorded.Action.ComputeHash(), recorded.ActionHash);
        Assert.Equal(snapshot.Events[0].ScopeId, recorded.Action.ScopeId);
        Assert.NotNull(WorkflowApprovalEvidence.Pending(snapshot));
        return snapshot;
    }

    private static RunSnapshot WaitingOnNestedApproval()
    {
        var childGraph = GraphEditing.InsertNodeAfter(PipelineTemplates.Create("uppercase") with { WorkflowId = "approval-child" },
            "input", "out", new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64))));
        var toolNodeId = childGraph.Nodes.Single(entry => entry.Value.Type == NodeTypes.McpTool).Key;
        childGraph = GraphEditing.InsertApprovalBefore(childGraph, toolNodeId);
        var child = WorkflowRevision.Create(childGraph, "fixture");
        var childCall = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var root = GraphEditing.InsertNodeAfter(PipelineTemplates.Create("uppercase") with { WorkflowId = "approval-parent" },
            "input", "out", childCall);
        var callNodeId = root.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var rootRevision = WorkflowRevision.Create(root, "fixture");
        var approval = child.Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Approval).Key;
        var runId = Guid.NewGuid();
        var action = new WorkflowApprovalAction(runId, rootRevision.RevisionId, rootRevision.ContentHash, approval, Guid.NewGuid(),
            toolNodeId, Guid.NewGuid(), child.Definition.Nodes[toolNodeId].Tool!, "input", WorkflowApprovalAction.HashInput("input"))
        {
            ExecutingRevisionId = child.RevisionId,
            ExecutingWorkflowHash = child.ContentHash,
            WorkflowPath = [new(callNodeId, child.RevisionId, child.ContentHash)]
        };
        var request = new WorkflowApprovalRequest(Guid.NewGuid(), action, action.ComputeHash(),
            child.Definition.Nodes[approval].Approval!.Prompt, DateTimeOffset.UtcNow);
        var header = new RunHeader(runId, rootRevision.RevisionId, null, "fixture", rootRevision.CreatedAt,
            rootRevision.ContentHash, root, "input");
        return new(header, [
            new(runId, rootRevision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Started),
            new(runId, rootRevision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.ApprovalRequested,
                approval, action.ApprovalInvocationId, Data: JsonSerializer.Serialize(request, JsonSerializerOptions.Web)) { ScopeId = callNodeId }
        ]);
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

    [Fact]
    public async Task NestedApprovalShowsChildScopeAndMarksTheParentCallNode()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowRunViewer.Snapshot)] = WaitingOnNestedApproval() }));
            return root.ToHtmlString();
        });

        Assert.Contains("Child awaiting approval", html);
        Assert.Contains("Nested workflow path:", html);
        Assert.Contains("Executing child revision:", html);
        Assert.Contains("vaw-waiting-approval", html);
    }

    private sealed class NeverDecide : IWorkflowApprovalClient
    {
        public Task DecideApprovalAsync(Guid runId, Guid requestId, string actionHash, bool approve) => throw new InvalidOperationException("Rendering cannot decide an action.");
    }
}
