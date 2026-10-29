using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphClipboardTests
{
    [Fact]
    public void Pure_fragment_round_trips_and_pastes_with_fresh_ids_and_layout()
    {
        var source = ExplicitWorkflow();
        var sourceLayout = WorkflowLayout.Create(source);
        var fragment = GraphClipboardEditing.Extract(source, sourceLayout, ["constant"], [],
            [new("result", "string", "constant", "out")]);

        var json = GraphClipboardJson.Serialize(fragment);
        var restored = GraphClipboardJson.Deserialize(json);
        var target = TargetWorkflow();
        var pasted = GraphClipboardEditing.Paste(target, WorkflowLayout.Create(target), restored, [],
            [new("result", "output", "in")], new(700, 300),
            id => "copy-" + id, id => "copy-edge-" + id);

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
