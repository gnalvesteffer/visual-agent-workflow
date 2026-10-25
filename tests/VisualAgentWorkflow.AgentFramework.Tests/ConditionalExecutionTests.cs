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
        var graph = GraphEditing.InsertConditionalAfter(baseline, "input", "out");
        var branch = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        graph = GraphEditing.InsertNodeAfter(graph, branch, "true", new(NodeTypes.Uppercase, "1.0.0"));
        return GraphEditing.InsertNodeAfter(graph, branch, "false", new(NodeTypes.Reverse, "1.0.0"));
    }

    private static WorkflowDefinition EmptyArmsGraph()
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
        Assert.Equal("YES", await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "yes",
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
        var graph = EmptyArmsGraph();
        var events = new List<ExecutionEvent>();
        Assert.Equal(input, await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), input,
            e => { events.Add(e); return ValueTask.CompletedTask; }));
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var expectedEdge = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == port).Key;
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;
        Assert.Equal(expectedEdge, Assert.Single(events, e => e.NodeId == merge && e.Kind == ExecutionEventKinds.Transfer).ConnectionId);
        Assert.Single(events, e => e.NodeId == merge && e.Kind == ExecutionEventKinds.NodeStarted);
    }

    [Fact]
    public async Task TerminationExitEndsSuccessfullyWithoutReturningOutput()
    {
        var baseline = EmptyArmsGraph();
        var graph = GraphEditing.InsertTerminationAfter(baseline,
            baseline.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key, "false");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteWithOutcomeAsync(runId, WorkflowRevision.Create(graph, "fixture"), "no",
            null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Null(result.Output);
        Assert.True(result.Terminated);
        Assert.Equal(ExecutionEventKinds.Terminated, events[^1].Kind);
        Assert.Equal(graph.Nodes.Single(node => node.Value.Type == NodeTypes.Terminate).Key, events[^1].NodeId);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.Data == "Workflow terminated without returning a value.");
        Assert.DoesNotContain(events, evt => evt.NodeId == "output");

        var legacyOutput = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "no",
            _ => ValueTask.CompletedTask);
        Assert.Equal(string.Empty, legacyOutput);
    }

    [Fact]
    public async Task NonTerminatingConditionArmStillReturnsTheWorkflowOutput()
    {
        var baseline = EmptyArmsGraph();
        var condition = baseline.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var graph = GraphEditing.InsertTerminationAfter(baseline, condition, "false");
        var result = await new PipelineRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "yes",
            null, _ => ValueTask.CompletedTask);

        Assert.Equal("yes", result.Output);
        Assert.False(result.Terminated);
    }

    [Theory]
    [InlineData("say yes", "SAY YES")]
    [InlineData("abc", "cba")]
    public async Task ConditionArmsCanTakeDifferentRoutesAndReturnDirectly(string input, string expected)
    {
        var graph = EmptyArmsGraph();
        var branch = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Merge).Key;
        graph = GraphEditing.RemoveNode(graph, merge);

        var upper = "upper";
        var reverse = "reverse";
        graph = graph with
        {
            Nodes = graph.Nodes.Add(upper, new(NodeTypes.Uppercase, "1.0.0")).Add(reverse, new(NodeTypes.Reverse, "1.0.0")),
            Connections = graph.Connections
                .Add("true-to-upper", new(new(branch, "true"), new(upper, "in"), "data"))
                .Add("upper-to-output", new(new(upper, "out"), new("output", "in"), "data"))
                .Add("false-to-reverse", new(new(branch, "false"), new(reverse, "in"), "data"))
                .Add("reverse-to-output", new(new(reverse, "out"), new("output", "in"), "data"))
        };

        var events = new List<ExecutionEvent>();
        Assert.Equal(expected, await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), input,
            e => { events.Add(e); return ValueTask.CompletedTask; }));
        var selected = input == "say yes" ? "upper" : "reverse";
        var skipped = selected == upper ? reverse : upper;
        Assert.Contains(events, e => e.NodeId == selected && e.Kind == ExecutionEventKinds.NodeCompleted);
        Assert.DoesNotContain(events, e => e.NodeId == skipped);
        Assert.Single(events, e => e.NodeId == "output" && e.Kind == ExecutionEventKinds.NodeStarted);
    }

    [Fact]
    public async Task ApprovalFailsClosedWhenDirectCallerSuppliesNoCoordinator()
    {
        var graph = Graph();
        var target = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Reverse).Key;
        graph = GraphEditing.ReplaceNode(graph, target, new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64))));
        graph = GraphEditing.InsertApprovalBefore(graph, target);
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "abc",
            e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.DoesNotContain(events, e => e.NodeId == target);
        Assert.DoesNotContain(events, e => e.Kind.StartsWith("tool.", StringComparison.Ordinal));
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
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
