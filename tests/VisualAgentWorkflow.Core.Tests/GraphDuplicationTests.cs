using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphDuplicationTests
{
    [Fact]
    public void Duplicate_node_copies_configuration_without_copying_connections()
    {
        var definition = new WorkflowDefinition(
            WorkflowSchemaVersions.ExplicitGraph, "duplicate", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["step"] = new(NodeTypes.TextConcat, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["left"] = new(new("input", "out"), new("step", "left"), "data"),
                ["right"] = new(new("input", "out"), new("step", "right"), "data"),
                ["last"] = new(new("step", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["execute"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

        var result = GraphEditing.DuplicateNode(definition, "step");

        Assert.NotEqual("step", result.NodeId);
        Assert.Equal(definition.Nodes["step"], result.Definition.Nodes[result.NodeId]);
        Assert.Equal(definition.Connections, result.Definition.Connections);
        Assert.Equal(definition.ExecutionConnections, result.Definition.ExecutionConnections);
        Assert.Contains(WorkflowValidator.Validate(result.Definition), issue => issue.Code == "data.unreachable");
    }
}
