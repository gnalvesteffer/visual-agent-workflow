using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ParallelBlockEditingTests
{
    private static WorkflowDefinition Baseline() => new("1.0", "parallel-block", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["after"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["incoming"] = Link("input", "out", "after"),
            ["unrelated"] = Link("after", "out", "output")
        }.ToImmutableSortedDictionary());

    [Fact]
    public void InsertedBlockHasNamedStringBranchesAndJsonJoinThenPreservesTextContinuation()
    {
        var baseline = Baseline();
        var graph = GraphEditing.InsertParallelBlockAfter(baseline, "input", "out");
        var fork = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var join = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        var anchors = graph.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelBranch).Select(entry => entry.Key).ToArray();
        var selector = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.JsonSelectString).Key;

        Assert.Equal("1.14", graph.SchemaVersion);
        Assert.Equal(2, anchors.Length);
        Assert.All(new[] { "branch_1", "branch_2" }, port =>
            Assert.Contains(graph.Connections.Values, edge => edge.Source == new PortReference(fork, port)));
        Assert.All(new[] { "branch_1", "branch_2" }, port =>
            Assert.Contains(graph.Connections.Values, edge => edge.Target == new PortReference(join, port)));
        Assert.Equal("json", WorkflowPorts.Outputs(graph.Nodes[join]).Single().DataType);
        Assert.Equal("", graph.Nodes[selector].JsonPropertySelection!.Pointer);
        Assert.Null(graph.Nodes["output"].OutputDataType);
        Assert.Empty(WorkflowValidator.Validate(graph));
    }

    [Fact]
    public void RemovingBlockRestoresTheOriginalSourceAndFollowingPath()
    {
        var baseline = Baseline();
        var graph = GraphEditing.InsertParallelBlockAfter(baseline, "input", "out");
        var fork = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var removed = ParallelBlockEditing.Remove(graph, fork);

        Assert.Equal(baseline.Nodes, removed.Nodes);
        var edge = Assert.Single(removed.Connections.Values, connection => connection.Source == new PortReference("input", "out"));
        Assert.Equal(new PortReference("after", "in"), edge.Target);
        Assert.Equal(2, removed.Connections.Count);
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void MismatchedJoinInputIsReportedAsAnIncompleteBranchIssue()
    {
        var graph = GraphEditing.InsertParallelBlockAfter(Baseline(), "input", "out");
        var join = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        var second = graph.Connections.Single(entry => entry.Value.Target == new PortReference(join, "branch_2"));
        graph = graph with
        {
            Connections = graph.Connections.SetItem(second.Key, second.Value with { Target = new(join, "branch_1") })
        };

        Assert.Contains(WorkflowValidator.Validate(graph), issue => issue.Code == "connection.duplicate" || issue.Code.StartsWith("parallel.branch", StringComparison.Ordinal));
    }

    [Fact]
    public void SequentialParallelBlocksUseSchema120AndCanBeRemovedIndependently()
    {
        var first = GraphEditing.InsertParallelBlockAfter(Baseline(), "input", "out");
        var firstFork = first.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var firstSelector = first.Nodes.Single(entry => entry.Value.Type == NodeTypes.JsonSelectString).Key;
        var second = GraphEditing.InsertParallelBlockAfter(first, firstSelector, "out");
        var forks = second.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelFork).Select(entry => entry.Key).ToArray();

        Assert.Equal("1.20", second.SchemaVersion);
        Assert.Equal(2, forks.Length);
        Assert.Empty(WorkflowValidator.Validate(second));
        Assert.Equal(5, ParallelBlockEditing.Nodes(second, firstFork).Length);

        var restored = ParallelBlockEditing.Remove(second, forks[1]);
        Assert.Single(restored.Nodes.Values, node => node.Type == NodeTypes.ParallelFork);
        Assert.Empty(WorkflowValidator.Validate(restored));

        var legacySchema = second with { SchemaVersion = "1.19" };
        Assert.Contains(WorkflowValidator.Validate(legacySchema), issue => issue.Code == "parallel.pair");
    }

    [Fact]
    public void ParallelBlocksCannotBeInsertedBeyondTheBoundedMaximum()
    {
        var graph = GraphEditing.InsertParallelBlockAfter(Baseline(), "input", "out");
        for (var index = 1; index < ParallelGraphLimits.MaximumBlocks; index++)
        {
            var edge = graph.Connections.Values.Single(connection => connection.Target.NodeId == "after");
            graph = GraphEditing.InsertParallelBlockAfter(graph, edge.Source.NodeId, edge.Source.PortId);
        }

        var latestEdge = graph.Connections.Values.Single(connection => connection.Target.NodeId == "after");
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertParallelBlockAfter(graph, latestEdge.Source.NodeId, latestEdge.Source.PortId));
    }

    [Fact]
    public void IndependentlyConcurrentParallelBlocksAreRejected()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("condition", new(NodeTypes.Condition, "1.0.0") { Condition = new("equals", "yes") })
            .Add("fork-a", new(NodeTypes.ParallelFork, "1.0.0"))
            .Add("fork-b", new(NodeTypes.ParallelFork, "1.0.0"))
            .Add("branch-a1", new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add("branch-a2", new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add("branch-b1", new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add("branch-b2", new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add("join-a", new(NodeTypes.ParallelJoin, "1.0.0"))
            .Add("join-b", new(NodeTypes.ParallelJoin, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var graph = new WorkflowDefinition("1.20", "parallel-order", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-condition", Link("input", "out", "condition"))
                .Add("true-fork", new(new("condition", "true"), new("fork-a", "in"), "data"))
                .Add("false-fork", new(new("condition", "false"), new("fork-b", "in"), "data"))
                .Add("a-fork-1", new(new("fork-a", "branch_1"), new("branch-a1", "in"), "data"))
                .Add("a-fork-2", new(new("fork-a", "branch_2"), new("branch-a2", "in"), "data"))
                .Add("a-join-1", new(new("branch-a1", "out"), new("join-a", "branch_1"), "data"))
                .Add("a-join-2", new(new("branch-a2", "out"), new("join-a", "branch_2"), "data"))
                .Add("b-fork-1", new(new("fork-b", "branch_1"), new("branch-b1", "in"), "data"))
                .Add("b-fork-2", new(new("fork-b", "branch_2"), new("branch-b2", "in"), "data"))
                .Add("b-join-1", new(new("branch-b1", "out"), new("join-b", "branch_1"), "data"))
                .Add("b-join-2", new(new("branch-b2", "out"), new("join-b", "branch_2"), "data"))
                .Add("a-output", new(new("join-a", "out"), new("output", "in"), "data"))
                .Add("b-output", new(new("join-b", "out"), new("output", "in"), "data")));

        Assert.Contains(WorkflowValidator.Validate(graph), issue => issue.Code == "parallel.blocks.order");
    }

    private static ConnectionDefinition Link(string source, string output, string target) =>
        new(new(source, output), new(target, "in"), "data");
}
