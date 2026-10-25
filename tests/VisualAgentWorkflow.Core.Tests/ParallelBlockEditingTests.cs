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

    private static ConnectionDefinition Link(string source, string output, string target) =>
        new(new(source, output), new(target, "in"), "data");
}
