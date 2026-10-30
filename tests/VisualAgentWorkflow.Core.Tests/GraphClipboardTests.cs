using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pure_fragment_round_trips_and_pastes_with_fresh_ids_and_layout(bool unified)
    {
        var source = ExplicitWorkflow();
        if (unified) source = WorkflowSchemaNormalization.NormalizeForEditing(source);
        var sourceLayout = WorkflowLayout.Create(source);
        var fragment = GraphClipboardEditing.Extract(source, sourceLayout, ["constant"], [],
            [new("result", "string", "constant", "out")]);

        var boundary = Assert.Single(fragment.Outputs);
        Assert.Equal("output", boundary.TargetNodeId);
        Assert.Equal("in", boundary.TargetPortId);

        var json = GraphClipboardJson.Serialize(fragment);
        var restored = GraphClipboardJson.Deserialize(json);
        Assert.Equal(boundary, Assert.Single(restored.Outputs));
        var target = TargetWorkflow();
        if (unified) target = WorkflowSchemaNormalization.NormalizeForEditing(target);
        var pasted = GraphClipboardEditing.Paste(target, WorkflowLayout.Create(target), restored, [],
            [new("result", "output", "in")], new(700, 300),
            id => "copy-" + id, id => "copy-edge-" + id);

        Assert.Equal(target.SchemaVersion, pasted.Definition.SchemaVersion);
        var pastedNode = Assert.Single(pasted.NodeIdMap.Values);
        Assert.Equal("copy-constant", pastedNode);
        Assert.NotEqual("constant", pastedNode);
        Assert.Equal(new(700, 300), pasted.Layout.Nodes[pastedNode].Position);
        Assert.Equal(pastedNode, pasted.Definition.Connections.Values.Single(edge => edge.Target.NodeId == "output").Source.NodeId);
        Assert.Empty(WorkflowValidator.Validate(pasted.Definition));
    }

    [Fact]
    public void Clipboard_rejects_effectful_nodes_and_execution_edges()
    {
        var source = ExplicitWorkflow() with
        {
            Nodes = ExplicitWorkflow().Nodes.SetItem("agent", new(NodeTypes.Agent, "1.0.0"))
        };
        var fragment = new GraphClipboardFragment("1.23",
            new Dictionary<string, NodeDefinition> { ["agent"] = source.Nodes["agent"] }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty,
            new Dictionary<string, GraphClipboardNodePresentation> { ["agent"] = new(new(0, 0)) }.ToImmutableSortedDictionary(),
            [], []);

        Assert.Contains(GraphClipboardEditing.Validate(fragment), issue => issue.Code == "clipboard.node.ineligible");
        Assert.Throws<ArgumentException>(() => GraphClipboardJson.Serialize(fragment));
    }

    [Fact]
    public void Multi_node_pure_fragment_preserves_internal_edges_and_boundary_hints()
    {
        var source = ExplicitWorkflow() with
        {
            Nodes = ExplicitWorkflow().Nodes.Remove("constant")
                .Add("left", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("left") })
                .Add("right", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("right") })
                .Add("join", new(NodeTypes.TextConcat, "1.0.0")),
            Connections = ExplicitWorkflow().Connections
                .SetItem("left-join", new(new("left", "out"), new("join", "left"), "data"))
                .SetItem("right-join", new(new("right", "out"), new("join", "right"), "data"))
                .SetItem("join-output", new(new("join", "out"), new("output", "in"), "data"))
                .Remove("data")
        };

        var fragment = GraphClipboardEditing.Extract(source, WorkflowLayout.Create(source), ["left", "right", "join"], [],
            [new("result", "string", "join", "out")]);

        Assert.Equal(3, fragment.Nodes.Count);
        Assert.Equal(2, fragment.Connections.Count);
        Assert.Equal("output", Assert.Single(fragment.Outputs).TargetNodeId);
        Assert.All(fragment.Connections.Values, edge =>
            Assert.Contains(edge.Source.NodeId, new[] { "left", "right", "join" }));
    }

    private static WorkflowDefinition ExplicitWorkflow() => new(
        WorkflowSchemaVersions.ExplicitGraph, "clipboard", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("copied") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["data"] = new(new("constant", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["execute"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition TargetWorkflow() => new(
        WorkflowSchemaVersions.ExplicitGraph, "clipboard-target", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["data"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["execute"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };
}
