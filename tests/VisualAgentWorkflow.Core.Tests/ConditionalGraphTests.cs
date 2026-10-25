using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ConditionalGraphTests
{
    private static WorkflowDefinition Baseline() => new("1.0", "conditional-test", "input",
        new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition> { ["edge"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary());
    private static WorkflowDefinition Graph() => GraphEditing.InsertConditionalAfter(Baseline(), "input", "out");

    [Fact]
    public void ConditionalGraphRoundTripsAndPackagesWithoutChangingLegacyHashes()
    {
        var baseline = Baseline();
        var json = WorkflowJson.Serialize(baseline);
        Assert.DoesNotContain("\"condition\":", json);
        var graph = Graph();
        Assert.Equal("1.4", graph.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(graph));
        Assert.Equal(4, WorkflowGraph.OrderedNodes(graph).Length);
        Assert.Equal(2, graph.Nodes.Values.Count(node => node.Type is NodeTypes.Condition or NodeTypes.Merge));
        Assert.DoesNotContain(graph.Nodes.Values, node => node.Type is NodeTypes.Uppercase or NodeTypes.Reverse);
        var layout = WorkflowLayout.Create(graph);
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(graph, layout));
        Assert.Equal(WorkflowJson.ComputeHash(graph), package.DefinitionHash);
        Assert.Equal(json, WorkflowJson.Serialize(WorkflowJson.Deserialize(json)));
        Assert.Throws<ArgumentException>(() => PipelineEditing.OrderedNodes(graph));
        Assert.Throws<ArgumentException>(() => PipelineEditing.InsertAfter(graph, "input", NodeTypes.Uppercase));
    }

    [Theory]
    [InlineData("equals", "yes", "yes", true)]
    [InlineData("equals", "yes", "YES", false)]
    [InlineData("contains", "yes", "say yes now", true)]
    [InlineData("starts-with", "yes", "say yes", false)]
    [InlineData("is-empty", "", "", true)]
    [InlineData("is-empty", "", " ", false)]
    public void ConditionsUseExplicitOrdinalSemantics(string operation, string operand, string text, bool expected) =>
        Assert.Equal(expected, new StringCondition(operation, operand).Evaluate(text));

    [Fact]
    public void OldSchemasAndExecutableExpressionsCannotEnableRouting()
    {
        var graph = Graph();
        Assert.Contains(WorkflowValidator.Validate(graph with { SchemaVersion = "1.3" }), i => i.Code == "node.schema");
        var id = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        foreach (var condition in new[] { new StringCondition("csharp", "File.Delete(path)"), new("contains", new string('a', 1025)), new("is-empty", "unexpected") })
            Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(id, graph.Nodes[id] with { Condition = condition }) }), i => i.Code == "condition.configuration");
        Assert.Contains(WorkflowValidator.Validate(Baseline() with { Nodes = Baseline().Nodes.SetItem("input", new(NodeTypes.Input, "1.0.0") { Condition = new("equals", "x") }) }), i => i.Code == "condition.unexpected");
    }

    [Fact]
    public void MissingArmsDuplicatePortsCyclesAndImplicitJoinsAreRejected()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var merge = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Merge).Key;
        var edge = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == "false");
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.Remove(edge.Key) }), i => i.Code == "graph.degree");
        var duplicatePort = edge.Value with { Source = new(branch, "true") };
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.SetItem(edge.Key, duplicatePort) }), i => i.Code == "graph.degree");
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(merge, new(NodeTypes.Reverse, "1.0.0")) }), i => i.Code == "graph.degree");
        var back = graph.Connections.Single(e => e.Value.Source.NodeId == merge);
        Assert.Contains(WorkflowValidator.Validate(graph with { Connections = graph.Connections.SetItem(back.Key, back.Value with { Target = new(branch, "in") }) }), i => i.Code == "graph.cycle");
    }

    [Fact]
    public void LocalEditsPreserveOtherArmIdentitiesAndNeverFlattenRouting()
    {
        var graph = Graph();
        var branch = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Condition).Key;
        var originalFalse = graph.Connections.Single(e => e.Value.Source.NodeId == branch && e.Value.Source.PortId == "false");
        var inserted = GraphEditing.InsertNodeAfter(graph, branch, "true", new(NodeTypes.Reverse, "1.0.0"));
        Assert.Equal(originalFalse.Value, inserted.Connections[originalFalse.Key]);
        var added = inserted.Nodes.Keys.Except(graph.Nodes.Keys).Single();
        var removed = GraphEditing.RemoveNode(inserted, added);
        Assert.Equal(WorkflowJson.Serialize(graph), WorkflowJson.Serialize(removed));
        var nested = GraphEditing.InsertConditionalAfter(graph, branch, "true");
        Assert.Empty(WorkflowValidator.Validate(nested));
        Assert.Equal(2, nested.Nodes.Values.Count(n => n.Type == NodeTypes.Condition));
        Assert.Throws<ArgumentException>(() => GraphEditing.RemoveNode(graph, branch));
    }
}
