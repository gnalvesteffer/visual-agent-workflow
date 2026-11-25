using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>Editing a shaped Input must never leave a shape on a node that is no longer json (Decision 0082 shaped json slice).</summary>
public sealed class ShapedJsonEditingTests
{
    private static readonly JsonObjectShape Shape = new([new("a", "A", "string", true)]);

    private static WorkflowDefinition Graph(bool withSelector)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = "json", InputShape = Shape })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = withSelector ? null : "json" });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty;
        if (withSelector)
        {
            nodes = nodes.Add("sel", new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new("/a") })
                .Add("up", new(NodeTypes.Uppercase, "1.0.0"));
            data = data.Add("c1", new(new("input", "out"), new("sel", "in"), "data"))
                .Add("c2", new(new("sel", "out"), new("up", "in"), "data"))
                .Add("c3", new(new("up", "out"), new("output", "in"), "data"));
        }
        else data = data.Add("c1", new(new("input", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("x", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "edit", "input", nodes, data) { ExecutionConnections = execution };
    }

    [Fact]
    public void Removing_the_selector_that_collapses_a_json_input_to_text_clears_the_shape()
    {
        var edited = GraphEditing.RemoveJsonSelector(Graph(true), "sel");
        Assert.Null(edited.Nodes["input"].InputDataType);
        Assert.Null(edited.Nodes["input"].InputShape);
        Assert.Empty(WorkflowValidator.Validate(edited));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("array<string>")]
    public void Changing_a_shaped_inputs_type_away_from_json_clears_the_shape_and_stays_editable(string type)
    {
        var edited = GraphEditing.SetBoundaryType(Graph(false), "input", type);
        Assert.Null(edited.Nodes["input"].InputShape);
        Assert.DoesNotContain(WorkflowValidator.Validate(edited), issue => issue.Code.StartsWith("object.shape", StringComparison.Ordinal));
        Assert.NotNull(WorkflowJson.SerializeDraft(edited));
    }

    [Fact]
    public void Keeping_the_json_type_keeps_the_shape()
    {
        var edited = GraphEditing.SetBoundaryType(Graph(false), "input", "json");
        Assert.Equal(Shape, edited.Nodes["input"].InputShape);
    }
}
