using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class BoundedLoopTests
{
    private static WorkflowDefinition Baseline() => new("1.0", "bounded-loop-test", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Fact]
    public void LoopInsertionCreatesOneBoundedBodyAndUsesSchema117()
    {
        var graph = GraphEditing.InsertBoundedLoopAfter(Baseline(), "input", "out");

        Assert.Equal("1.17", graph.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(graph));
        Assert.Single(graph.Nodes.Values, node => node.Type == NodeTypes.Loop);
        Assert.Single(graph.Nodes.Values, node => node.Type == NodeTypes.LoopBody);
        Assert.Contains(graph.Connections.Values, edge => edge.Source.NodeId == "input" && edge.Target.NodeId == graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key);
        Assert.Equal(graph.Nodes.Count, WorkflowGraph.OrderedNodes(graph).Length);
        Assert.Equal(3, graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Value.Loop!.MaximumIterations);
    }

    [Fact]
    public void LoopPredicateAndMaximumAreValidatedAndOtherCyclesRemainInvalid()
    {
        var graph = GraphEditing.InsertBoundedLoopAfter(Baseline(), "input", "out");
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var loop = graph.Nodes[loopId];

        Assert.Contains(WorkflowValidator.Validate(graph with { SchemaVersion = "1.16" }), issue => issue.Code == "loop.schema");
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(loopId, loop with { Loop = loop.Loop! with { MaximumIterations = WorkflowLoopLimits.MaximumIterations + 1 } }) }), issue => issue.Code == "loop.configuration");
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(loopId, loop with { Loop = loop.Loop! with { ContinueWhile = new("regex", ".*") } }) }), issue => issue.Code == "loop.configuration");

        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        var anchorEdge = graph.Connections.Single(pair => pair.Value.Source.NodeId == anchorId);
        var invalidCycle = graph with { Connections = graph.Connections.SetItem(anchorEdge.Key,
            anchorEdge.Value with { Target = new(anchorId, "in") }) };
        Assert.Contains(WorkflowValidator.Validate(invalidCycle), issue => issue.Code == "graph.cycle");
    }

    [Fact]
    public void RemovingLoopKeepsTheBodyAsOneOrdinaryPass()
    {
        var graph = GraphEditing.InsertBoundedLoopAfter(Baseline(), "input", "out");
        var anchorId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        graph = GraphEditing.InsertNodeAfter(graph, anchorId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var bodyId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse).Key;
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;

        var removed = GraphEditing.RemoveNode(graph, loopId);

        Assert.Empty(WorkflowValidator.Validate(removed));
        Assert.DoesNotContain(removed.Nodes.Values, node => node.Type is NodeTypes.Loop or NodeTypes.LoopBody);
        Assert.Contains(removed.Connections.Values, edge => edge.Source.NodeId == "input" && edge.Target.NodeId == bodyId);
        Assert.Contains(removed.Connections.Values, edge => edge.Source.NodeId == bodyId && edge.Target.NodeId == "output");
    }

    [Fact]
    public void LoopConfigurationCanBeEditedWithoutBreakingTheBody()
    {
        var graph = GraphEditing.InsertBoundedLoopAfter(Baseline(), "input", "out");
        var loopId = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var edited = GraphEditing.ReplaceNode(graph, loopId, graph.Nodes[loopId] with
        { Loop = graph.Nodes[loopId].Loop! with { ContinueWhile = new("equals", "again"), MaximumIterations = 1 } });

        Assert.Empty(WorkflowValidator.Validate(edited));
        Assert.Equal("equals", edited.Nodes[loopId].Loop!.ContinueWhile.Operator);
        Assert.Equal(1, edited.Nodes[loopId].Loop!.MaximumIterations);
    }

    [Fact]
    public void IncompleteLoopDraftWithNullConnectionRemainsInspectableAndReportsValidation()
    {
        var graph = GraphEditing.InsertBoundedLoopAfter(Baseline(), "input", "out");
        var malformed = graph with { Connections = graph.Connections.SetItem("invalid-edge", null!) };

        Assert.Contains(WorkflowValidator.Validate(malformed), issue => issue.Code == "connection.reference");
        Assert.Equal(malformed.Nodes.Count, WorkflowGraph.DisplayOrder(malformed).Length);
    }
}
