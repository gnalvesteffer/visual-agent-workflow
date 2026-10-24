using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ConditionalExecutionTests
{
    private static WorkflowDefinition Graph()
    {
        var baseline = new WorkflowDefinition("1.0", "conditional-runtime", "input",
            new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition> { ["edge"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary());
        return GraphEditing.InsertConditionalAfter(baseline, "input", "out");
    }

    [Theory]
    [InlineData("say yes", "true", "SAY YES")]
    [InlineData("abc", "false", "cba")]
    public async Task FrameworkFollowsOnlyChosenArmAndMergeRecordsActualIncomingEdge(string input, string port, string expected)
    {
        var graph = Graph();
        var revision = WorkflowRevision.Create(graph, "fixture");
        var events = new List<ExecutionEvent>();
        Assert.Equal(expected, await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), revision, input, e => { events.Add(e); return ValueTask.CompletedTask; }));
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var chosen = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == port);
        var skipped = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId != port);
        Assert.DoesNotContain(events, e => e.NodeId == skipped.Value.Target.NodeId);
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.Transfer && e.ConnectionId == chosen.Key);
        Assert.DoesNotContain(events, e => e.ConnectionId == skipped.Key);
        var decision = Assert.Single(events, e => e.Kind == ExecutionEventKinds.BranchSelected);
        Assert.Equal(port, JsonDocument.Parse(decision.Data!).RootElement.GetProperty("port").GetString());
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;
        var mergeEdge = graph.Connections.Single(e => e.Value.Source.NodeId == chosen.Value.Target.NodeId && e.Value.Target.NodeId == merge).Key;
        Assert.Equal(mergeEdge, Assert.Single(events, e => e.NodeId == merge && e.Kind == ExecutionEventKinds.Transfer).ConnectionId);
        Assert.Equal(5, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Single(events, e => e.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task UnchosenArmDoesNotCreateAgentOrToolScopes()
    {
        var graph = Graph();
        var unchosen = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Reverse).Key;
        graph = GraphEditing.ReplaceNode(graph, unchosen, new(NodeTypes.McpTool, "1.0.0", new("unavailable.tool", new string('0', 64))));
        var events = new List<ExecutionEvent>();
        Assert.Equal("YES", await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "yes", e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.DoesNotContain(events, e => e.NodeId == unchosen);
        Assert.DoesNotContain(events, e => e.Kind.StartsWith("tool.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationAtChosenBoundaryDoesNotExecuteEitherArm()
    {
        var graph = Graph();
        var chosen = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Uppercase).Key;
        var gate = new BoundaryGate(chosen);
        var events = new List<ExecutionEvent>();
        using var cancellation = new CancellationTokenSource();
        var execution = new PipelineRunner().ExecuteControlledAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "yes",
            e => { events.Add(e); return ValueTask.CompletedTask; }, gate, cancellation.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(events, e => e.Kind == ExecutionEventKinds.BranchSelected);
        Assert.DoesNotContain(events, e => e.NodeId == chosen);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.Completed);
        Assert.Equal(ExecutionEventKinds.Cancelled, events[^1].Kind);
    }

    [Fact]
    public async Task NestedConditionalsMergeOneValueAndRecordEachDecisionOnce()
    {
        var graph = Graph();
        var upper = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Uppercase).Key;
        graph = GraphEditing.InsertConditionalAfter(graph, upper, "out");
        var events = new List<ExecutionEvent>();
        Assert.Equal("SEY", await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "yes",
            e => { events.Add(e); return ValueTask.CompletedTask; }));
        var ports = events.Where(e => e.Kind == ExecutionEventKinds.BranchSelected)
            .Select(e => JsonDocument.Parse(e.Data!).RootElement.GetProperty("port").GetString()).ToArray();
        Assert.Equal(new[] { "true", "false" }, ports);
        Assert.All(events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).GroupBy(e => e.NodeId), group => Assert.Single(group));
        Assert.Equal(2, events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted && graph.Nodes[e.NodeId!].Type == NodeTypes.Merge));
    }

    [Theory]
    [InlineData("yes", "true")]
    [InlineData("abc", "false")]
    public async Task EmptyArmsCanBothConnectDirectlyToTheSameMerge(string input, string port)
    {
        var graph = Graph();
        foreach (var id in graph.Nodes.Where(n => n.Value.Type is NodeTypes.Uppercase or NodeTypes.Reverse).Select(n => n.Key).ToArray())
            graph = GraphEditing.RemoveNode(graph, id);
        var events = new List<ExecutionEvent>();
        Assert.Equal(input, await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), input,
            e => { events.Add(e); return ValueTask.CompletedTask; }));
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var expectedEdge = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == port).Key;
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;
        Assert.Equal(expectedEdge, Assert.Single(events, e => e.NodeId == merge && e.Kind == ExecutionEventKinds.Transfer).ConnectionId);
        Assert.Single(events, e => e.NodeId == merge && e.Kind == ExecutionEventKinds.NodeStarted);
    }

    private sealed class BoundaryGate(string node) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != node) return;
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
