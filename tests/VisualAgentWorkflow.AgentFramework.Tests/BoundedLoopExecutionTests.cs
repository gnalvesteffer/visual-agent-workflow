using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class BoundedLoopExecutionTests
{
    private static WorkflowDefinition Graph()
    {
        var baseline = new WorkflowDefinition("1.0", "bounded-loop-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["edge"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        return GraphEditing.InsertBoundedLoopAfter(baseline, "input", "out");
    }

    [Fact]
    public async Task FrameworkRepeatsBodyAndStopsWhenPredicateBecomesFalse()
    {
        var graph = Graph();
        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        graph = GraphEditing.InsertNodeAfter(graph, anchorId, "out", new(NodeTypes.Uppercase, "1.0.0"));
        var bodyId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Uppercase).Key;
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("CONTINUE", output);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal("predicate-false", JsonDocument.Parse(stopped.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, events.Count(evt => evt.NodeId == bodyId && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task FrameworkEnforcesMaximumIterationsAndRecordsEveryPass()
    {
        var graph = Graph();
        var events = new List<ExecutionEvent>();
        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        var stopData = JsonDocument.Parse(stopped.Data!).RootElement;
        Assert.Equal("maximum-iterations", stopData.GetProperty("reason").GetString());
        Assert.Equal(3, stopData.GetProperty("iterations").GetInt32());
        Assert.Equal(3, events.Count(evt => evt.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key && evt.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }
}
