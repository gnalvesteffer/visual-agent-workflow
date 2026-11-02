using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class BoundedLoopExecutionTests
{
    private static WorkflowDefinition ExplicitLoopGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["set-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["output-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["loop-body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["body-set"] = new(new("body", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-loop"] = new(new("get", "then"), new("loop", "execute"), "execution"),
            ["loop-output"] = new(new("loop", "done"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.LoopVariablesGraph, "loop-variable-candidate", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

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
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(graph);
        var events = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph, "fixture"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-predicate-loop"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("CONTINUE", output);
        Assert.Equal(output, normalizedOutput);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal("predicate-false", JsonDocument.Parse(stopped.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, events.Count(evt => evt.NodeId == bodyId && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
        Assert.Equal(events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)), normalizedEvents
            .Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)));
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

    [Fact]
    public async Task LegacyDataLoopReevaluatesBodyTransformsOnEveryIteration()
    {
        var graph = Graph();
        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        graph = GraphEditing.InsertNodeAfter(graph, anchorId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var firstReverseId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse).Key;
        graph = GraphEditing.InsertNodeAfter(graph, firstReverseId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var secondReverseId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse && pair.Key != firstReverseId).Key;
        var reverseIds = new[] { firstReverseId, secondReverseId };
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(graph);
        var events = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "legacy-loop-transform"), "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-loop-transform"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        Assert.Equal(output, normalizedOutput);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        Assert.Equal(3, events.Count(evt => evt.NodeId == reverseIds[0] && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(3, events.Count(evt => evt.NodeId == reverseIds[1] && evt.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(new[] { "eunitnoc", "eunitnoc", "eunitnoc" }, events
            .Where(evt => evt.NodeId == reverseIds[0] && evt.Kind == ExecutionEventKinds.NodeCompleted)
            .Select(evt => evt.Data));
        Assert.Equal(new[] { "continue", "continue", "continue" }, events
            .Where(evt => evt.NodeId == reverseIds[1] && evt.Kind == ExecutionEventKinds.NodeCompleted)
            .Select(evt => evt.Data));
        var stop = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Equal("maximum-iterations", JsonDocument.Parse(stop.Data!).RootElement.GetProperty("reason").GetString());
        Assert.Equal("continue", events.Last(evt => evt.NodeId == loopId && evt.Kind == ExecutionEventKinds.NodeStarted).Data);
        Assert.Equal(events.Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)), normalizedEvents
            .Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
            .Select(evt => (evt.Kind, evt.Data)));
        foreach (var (nodeId, expectedValues) in new[]
        {
            (reverseIds[0], new[] { "eunitnoc", "eunitnoc", "eunitnoc" }),
            (reverseIds[1], new[] { "continue", "continue", "continue" })
        })
        {
            var recordedValues = normalizedEvents.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == nodeId)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("value").GetString());
            Assert.Equal(expectedValues, recordedValues);
        }
    }

    [Fact]
    public async Task UnifiedSchemaNormalizationPreservesExplicitLegacyLoopExecutionEvidence()
    {
        var legacy = ExplicitLoopGraph();
        var sourceJson = WorkflowJson.Serialize(legacy);
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(legacy);
        var legacyEvents = new List<ExecutionEvent>();
        var normalizedEvents = new List<ExecutionEvent>();

        var legacyOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(legacy, "legacy-loop"), "continue",
            evt => { legacyEvents.Add(evt); return ValueTask.CompletedTask; });
        var normalizedOutput = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(normalized, "normalized-loop"), "continue",
            evt => { normalizedEvents.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(legacy.Nodes.Keys, normalized.Nodes.Keys);
        Assert.Equal(legacy.Connections.Keys, normalized.Connections.Keys);
        Assert.Equal(legacy.Connections, normalized.Connections);
        Assert.Equal(legacy.ExecutionConnections!.Keys, normalized.ExecutionConnections!.Keys);
        Assert.Equal(legacy.ExecutionConnections, normalized.ExecutionConnections);
        Assert.Equal(sourceJson, WorkflowJson.Serialize(legacy));
        Assert.Equal(legacyOutput, normalizedOutput);
        Assert.Equal(legacyEvents.Select(evt => (evt.Kind, evt.NodeId, evt.Data)),
            normalizedEvents.Select(evt => (evt.Kind, evt.NodeId, evt.Data)));
    }
}
