using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowArrayLiteralTests
{
    [Theory]
    [InlineData("string", "[\"a\",\"b\"]", true)]
    [InlineData("boolean", "[true,false]", true)]
    [InlineData("integer", "[-1,0,9223372036854775807]", true)]
    [InlineData("number", "[1.5,-2e3]", true)]
    [InlineData("string", "[1]", false)]
    [InlineData("boolean", "[null]", false)]
    [InlineData("integer", "[1.5]", false)]
    [InlineData("number", "[1e999]", false)]
    [InlineData("json", "[1]", false)]
    [InlineData("string", "[[\"nested\"]]", false)]
    public void Literal_parser_accepts_only_bounded_non_null_homogeneous_primitive_arrays(string type, string json, bool expected)
    {
        Assert.Equal(expected, WorkflowArrayLiteral.TryParse(new(type, json), out _, out _));
    }

    [Fact]
    public void Typed_array_pins_resolve_from_the_declared_element_type()
    {
        var literal = new NodeDefinition(NodeTypes.ArrayLiteral, "1.0.0")
        { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\"]") };
        var get = new NodeDefinition(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
        var length = new NodeDefinition(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = WorkflowDataTypes.String };

        Assert.Equal("array<string>", Assert.Single(WorkflowPorts.ExplicitDataOutputs(literal)).DataType);
        Assert.Collection(WorkflowPorts.ExplicitDataInputs(get),
            port => Assert.Equal("array<string>", port.DataType),
            port => Assert.Equal(WorkflowDataTypes.Integer, port.DataType));
        Assert.Equal(WorkflowDataTypes.String, Assert.Single(WorkflowPorts.ExplicitDataOutputs(get)).DataType);
        Assert.Equal("array<string>", Assert.Single(WorkflowPorts.ExplicitDataInputs(length)).DataType);
        Assert.Empty(WorkflowValidator.Validate(ArrayGraph() with
        {
            Nodes = ArrayGraph().Nodes.SetItem("literal", literal).SetItem("get", get)
                .SetItem("length", length)
        }));
    }

    [Fact]
    public void Typed_array_schema_rejects_collection_outputs_and_execution_regions()
    {
        var graph = ArrayGraph();
        var collectionOutput = graph with
        {
            Nodes = graph.Nodes.SetItem("output", graph.Nodes["output"] with { OutputDataType = "array<string>" })
        };
        Assert.Contains(WorkflowValidator.Validate(collectionOutput), issue => issue.Code == "output.dataType");

        var withSequence = graph with
        {
            Nodes = graph.Nodes.Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            { Sequence = new([new("one", "One"), new("two", "Two")]) })
        };
        Assert.Contains(WorkflowValidator.Validate(withSequence), issue => issue.Code == "array.placement");
    }

    [Fact]
    public void Literal_bounds_and_invalid_draft_round_trip_are_enforced()
    {
        Assert.True(WorkflowArrayLiteral.TryParse(new(WorkflowDataTypes.String, "[]"), out _, out _));
        Assert.True(WorkflowArrayLiteral.TryParse(new(WorkflowDataTypes.Integer,
            "[" + string.Join(',', Enumerable.Range(0, WorkflowJsonArrayLimits.MaximumItems)) + "]"), out _, out _));
        Assert.False(WorkflowArrayLiteral.TryParse(new(WorkflowDataTypes.Integer,
            "[" + string.Join(',', Enumerable.Range(0, WorkflowJsonArrayLimits.MaximumItems + 1)) + "]"), out _, out _));
        Assert.False(WorkflowArrayLiteral.TryParse(new(WorkflowDataTypes.String,
            "[\"" + new string('x', WorkflowArrayLiteral.MaximumPayloadBytes) + "\"]"), out _, out _));

        var graph = ArrayGraph();
        var invalid = graph with
        {
            Nodes = graph.Nodes.SetItem("literal", graph.Nodes["literal"] with
            { ArrayLiteral = new(WorkflowDataTypes.String, "[null]") })
        };
        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "array.literal");
        var serialized = WorkflowJson.SerializeDraft(invalid);
        Assert.Equal(serialized, WorkflowJson.SerializeDraft(WorkflowJson.DeserializeDraft(serialized)));
    }

    [Fact]
    public void Typed_array_element_types_must_match_across_connections()
    {
        var graph = ArrayGraph();
        var mismatch = graph with
        {
            Nodes = graph.Nodes.SetItem("literal", graph.Nodes["literal"] with
            { ArrayLiteral = new(WorkflowDataTypes.Number, "[1.5]") })
        };
        Assert.Contains(WorkflowValidator.Validate(mismatch), issue => issue.Code == "connection.port");
    }

    [Fact]
    public void Array_literal_properties_can_be_edited_on_an_unconnected_draft_node()
    {
        var graph = ArrayGraph();
        var draft = graph with
        {
            Nodes = graph.Nodes.Where(pair => pair.Key is "input" or "output")
                .ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
        };
        var added = GraphEditing.AddUnconnectedNode(draft,
            new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[]") });
        var id = added.Nodes.Single(pair => pair.Key.StartsWith(NodeTypes.ArrayLiteral, StringComparison.Ordinal)).Key;
        var edited = GraphEditing.ReplaceNode(added, id,
            added.Nodes[id] with { ArrayLiteral = new(WorkflowDataTypes.Integer, "[1,2]") });

        Assert.Equal("integer", edited.Nodes[id].ArrayLiteral?.ElementType);
        Assert.Equal("[1,2]", edited.Nodes[id].ArrayLiteral?.ValuesJson);
    }

    private static WorkflowDefinition ArrayGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["literal"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\"]") },
            ["index"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(0) },
            ["get"] = new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["length"] = new(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["length-text"] = new(NodeTypes.IntegerToString, "1.0.0"),
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["literal-get"] = new(new("literal", "out"), new("get", "array"), "data"),
            ["index-get"] = new(new("index", "out"), new("get", "index"), "data"),
            ["get-length"] = new(new("literal", "out"), new("length", "in"), "data"),
            ["get-concat"] = new(new("get", "item"), new("concat", "left"), "data"),
            ["length-text"] = new(new("length", "out"), new("length-text", "in"), "data"),
            ["text-concat"] = new(new("length-text", "out"), new("concat", "right"), "data"),
            ["concat-output"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-array", "input", nodes, data)
        { ExecutionConnections = execution };
    }
}
