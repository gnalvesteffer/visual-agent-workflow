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
    public void Typed_array_schema_admits_typed_workflow_input_and_output_boundaries()
    {
        var graph = ArrayGraph();
        var boundaryGraph = TypedArrayBoundaryGraph();
        Assert.Empty(WorkflowValidator.Validate(boundaryGraph));

        var mismatchedOutput = boundaryGraph with
        {
            Nodes = boundaryGraph.Nodes.SetItem("output", boundaryGraph.Nodes["output"] with { OutputDataType = "array<boolean>" })
        };
        Assert.Contains(WorkflowValidator.Validate(mismatchedOutput), issue => issue.Code == "connection.port");

        var jsonArrayBoundary = boundaryGraph with
        {
            Nodes = boundaryGraph.Nodes
                .SetItem("input", boundaryGraph.Nodes["input"] with { InputDataType = WorkflowDataTypes.JsonArray })
                .SetItem("output", boundaryGraph.Nodes["output"] with { OutputDataType = WorkflowDataTypes.JsonArray })
        };
        Assert.Contains(WorkflowValidator.Validate(jsonArrayBoundary), issue => issue.Code == "input.dataType");
        Assert.Contains(WorkflowValidator.Validate(jsonArrayBoundary), issue => issue.Code == "output.dataType");

        var withSequence = graph with
        {
            Nodes = graph.Nodes.Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            { Sequence = new([new("one", "One"), new("two", "Two")]) })
        };
        Assert.Contains(WorkflowValidator.Validate(withSequence), issue => issue.Code == "array.placement");
    }

    [Fact]
    public void Typed_array_boundary_types_can_be_changed_through_graph_editing()
    {
        var definition = TypedArrayBoundaryGraph() with
        {
            Nodes = TypedArrayBoundaryGraph().Nodes
                .SetItem("input", new(NodeTypes.Input, "1.0.0"))
                .SetItem("output", new(NodeTypes.Output, "1.0.0"))
        };

        var withTypedInput = GraphEditing.ReplaceNode(definition, "input",
            definition.Nodes["input"] with { InputDataType = "array<string>" });
        Assert.Contains(WorkflowValidator.Validate(withTypedInput), issue => issue.Code == "connection.port");
        Assert.Equal("array<string>", WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(withTypedInput))
            .Nodes["input"].InputDataType);

        var complete = GraphEditing.ReplaceNode(withTypedInput, "output",
            withTypedInput.Nodes["output"] with { OutputDataType = "array<string>" });
        Assert.Empty(WorkflowValidator.Validate(complete));
        Assert.Equal("array<string>", Assert.Single(WorkflowPorts.ExplicitDataOutputs(complete.Nodes["input"])).DataType);
        Assert.Equal("array<string>", Assert.Single(WorkflowPorts.ExplicitDataInputs(complete.Nodes["output"])).DataType);
    }

    [Fact]
    public void Typed_array_input_boundaries_are_current_schema_only()
    {
        var graph = TypedArrayBoundaryGraph() with { SchemaVersion = "1.9" };
        Assert.Contains(WorkflowValidator.Validate(graph), issue => issue.Code == "input.dataType");
        Assert.Contains(WorkflowValidator.Validate(graph), issue => issue.Code == "output.dataType");
    }

    [Fact]
    public void Typed_array_boundary_types_survive_package_round_trip_and_change_definition_hash()
    {
        var definition = TypedArrayBoundaryGraph();
        var hash = WorkflowJson.ComputeHash(definition);
        var packageJson = WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition));
        var package = WorkflowPackageJson.Deserialize(packageJson);

        Assert.Equal(definition.Nodes["input"].InputDataType, package.Definition.Nodes["input"].InputDataType);
        Assert.Equal(definition.Nodes["output"].OutputDataType, package.Definition.Nodes["output"].OutputDataType);
        Assert.Equal(hash, WorkflowJson.ComputeHash(package.Definition));
        Assert.Equal(packageJson, WorkflowPackageJson.Serialize(package.Definition, package.Layout));

        var changedType = definition with
        {
            Nodes = definition.Nodes
                .SetItem("input", definition.Nodes["input"] with { InputDataType = "array<boolean>" })
                .SetItem("output", definition.Nodes["output"] with { OutputDataType = "array<boolean>" })
        };
        Assert.NotEqual(hash, WorkflowJson.ComputeHash(changedType));
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

    private static WorkflowDefinition TypedArrayBoundaryGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "array<string>" },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "array<string>" }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-output-value", new(new("input", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-output-execution", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-array-boundary", "input", nodes, data)
        { ExecutionConnections = execution };
    }
}
