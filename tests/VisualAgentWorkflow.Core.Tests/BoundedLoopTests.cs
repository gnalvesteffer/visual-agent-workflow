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

    [Fact]
    public void UnifiedLoopMayContainOneOwnedForkJoinThatReturnsThroughItsJoin()
    {
        var graph = LoopWithParallelBody();

        Assert.Empty(WorkflowValidator.Validate(graph));
    }

    [Fact]
    public void UnifiedLoopRejectsParallelRegionsOutsideItsBodyOrBranchFeedbackEscapes()
    {
        var graph = LoopWithParallelBody();
        var directFeedback = graph with
        {
            ExecutionConnections = graph.ExecutionConnections!
                .SetItem("agent-a-join", new(new("agent-a", "out"), new("loop", "feedback"), "execution"))
        };

        var issues = WorkflowValidator.Validate(directFeedback);

        Assert.Contains(issues, issue => issue.Code.StartsWith("parallel.", StringComparison.Ordinal) ||
            issue.Code == "loop.body.path");

        var outsideRegion = LoopWithParallelBody() with
        {
            Nodes = LoopWithParallelBody().Nodes.Add("detached-anchor",
                new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "detached") })
        };
        Assert.Contains(WorkflowValidator.Validate(outsideRegion), issue => issue.Code == "loop.parallel.placement");
    }

    private static WorkflowDefinition LoopWithParallelBody()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("a", "A", "anchor-a"),
            new ParallelBranchDescriptor("b", "B", "anchor-b"));
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 2) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) },
            ["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["loop-body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["body-fork"] = new(new("body", "then"), new("fork", "execute"), "execution"),
            ["fork-a"] = new(new("fork", ParallelBranchLimits.PinId("a")), new("anchor-a", "execute"), "execution"),
            ["fork-b"] = new(new("fork", ParallelBranchLimits.PinId("b")), new("anchor-b", "execute"), "execution"),
            ["anchor-a-agent"] = new(new("anchor-a", "then"), new("agent-a", "execute"), "execution"),
            ["anchor-b-agent"] = new(new("anchor-b", "then"), new("agent-b", "execute"), "execution"),
            ["agent-a-join"] = new(new("agent-a", "out"), new("join", ParallelBranchLimits.PinId("a")), "execution"),
            ["agent-b-join"] = new(new("agent-b", "out"), new("join", ParallelBranchLimits.PinId("b")), "execution"),
            ["join-loop"] = new(new("join", "then"), new("loop", "feedback"), "execution"),
            ["loop-output"] = new(new("loop", "done"), new("output", "execute"), "execution")
        };
        nodes["anchor-a"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "a") };
        nodes["anchor-b"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "b") };
        nodes["agent-a"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "A", []));
        nodes["agent-b"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "B", []));
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["join-output"] = new(new("join", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "loop-parallel", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }
}
