using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ConditionalBlockEditingTests
{
    private static WorkflowDefinition Baseline() => new("1.4", "block-removal", "input",
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
    public void RemovingEmptyBlockRestoresOriginalGraphAndLeavesSourceImmutable()
    {
        var baseline = Baseline();
        var graph = GraphEditing.InsertConditionalAfter(baseline, "input", "out");
        var before = WorkflowJson.Serialize(graph);
        var condition = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;

        Assert.Equal(2, ConditionalBlockEditing.Nodes(graph, condition).Length);
        var removed = ConditionalBlockEditing.Remove(graph, condition);

        Assert.Equal(WorkflowJson.Serialize(baseline), WorkflowJson.Serialize(removed));
        Assert.Equal(before, WorkflowJson.Serialize(graph));
    }

    [Fact]
    public void RemovingOuterBlockIncludesNestedDecisionsAndBothArmsButPreservesFollowingBlock()
    {
        var graph = GraphEditing.InsertConditionalAfter(Baseline(), "input", "out");
        var outer = graph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        graph = GraphEditing.InsertConditionalAfter(graph, outer, "true");
        graph = GraphEditing.InsertNodeAfter(graph, outer, "false", new(NodeTypes.Reverse, "1.0.0"));
        var expected = GraphEditing.InsertConditionalAfter(Baseline(), "after", "out");
        var followingNodes = expected.Nodes.Where(node => !Baseline().Nodes.ContainsKey(node.Key));
        var followingEdges = expected.Connections.Where(edge => edge.Key != "incoming");
        graph = graph with
        {
            Nodes = graph.Nodes.AddRange(followingNodes),
            Connections = graph.Connections.Remove("unrelated").AddRange(followingEdges)
        };

        var block = ConditionalBlockEditing.Nodes(graph, outer);
        var removed = ConditionalBlockEditing.Remove(graph, outer);

        Assert.Equal(5, block.Length);
        Assert.Equal(WorkflowJson.Serialize(expected), WorkflowJson.Serialize(removed));
    }

    [Fact]
    public void RemovingNestedBlockPreservesOuterDecisionAndSiblingArm()
    {
        var outerGraph = GraphEditing.InsertConditionalAfter(Baseline(), "input", "out");
        var outer = outerGraph.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        var nestedGraph = GraphEditing.InsertConditionalAfter(outerGraph, outer, "true");
        var nested = nestedGraph.Nodes.Keys.Except(outerGraph.Nodes.Keys).Single(id => nestedGraph.Nodes[id].Type == NodeTypes.Condition);
        nestedGraph = GraphEditing.InsertNodeAfter(nestedGraph, nested, "false", new(NodeTypes.Reverse, "1.0.0"));

        var removed = ConditionalBlockEditing.Remove(nestedGraph, nested);

        Assert.Equal(WorkflowJson.Serialize(outerGraph), WorkflowJson.Serialize(removed));
    }

    [Fact]
    public void SharedBranchCannotBeDeletedAsAnIndependentBlock()
    {
        var graph = new WorkflowDefinition("1.4", "shared-merges", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["outer"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") },
                ["inner"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("is-empty", "") },
                ["shared"] = new(NodeTypes.Merge, "1.0.0"),
                ["end"] = new(NodeTypes.Merge, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = Link("input", "out", "outer"),
                ["b"] = Link("outer", "true", "inner"),
                ["c"] = Link("outer", "false", "shared"),
                ["d"] = Link("inner", "true", "shared"),
                ["e"] = Link("inner", "false", "end"),
                ["f"] = Link("shared", "out", "end"),
                ["g"] = Link("end", "out", "output")
            }.ToImmutableSortedDictionary());
        Assert.Empty(WorkflowValidator.Validate(graph));
        var before = WorkflowJson.Serialize(graph);

        Assert.Throws<ArgumentException>(() => ConditionalBlockEditing.Nodes(graph, "inner"));
        Assert.Throws<ArgumentException>(() => ConditionalBlockEditing.Remove(graph, "inner"));
        Assert.Equal(before, WorkflowJson.Serialize(graph));
        Assert.Equal(2, ConditionalBlockEditing.Remove(graph, "outer").Nodes.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("input")]
    [InlineData("after")]
    [InlineData("output")]
    public void NonConditionsAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => ConditionalBlockEditing.Remove(Baseline(), id));

    private static ConnectionDefinition Link(string source, string port, string target) => new(new(source, port), new(target, "in"), "data");
}
