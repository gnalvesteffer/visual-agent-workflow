using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Shaped json first slice (Decision 0082, P5): a closed-object annotation on a schema 2.0 json Input and a pure
/// <c>core.object.get</c> addressed by stable field ID. Compatibility: an old reader rejects the unknown property and node type
/// (see <see cref="UnknownNodeAnnotationTests"/>), so there is no schema or capability gate.
/// </summary>
public sealed class ShapedJsonObjectTests
{
    private static JsonObjectFieldShape Field(string id, string type, bool required = true, string? name = null) =>
        new(id, name ?? $"Display {id}", type, required);

    private static JsonObjectShape Shape(params JsonObjectFieldShape[] fields) => new([.. fields]);

    private static readonly JsonObjectShape Order = Shape(
        Field("customer-id", "string"), Field("quantity", "integer"), Field("price", "number"),
        Field("express", "boolean"), Field("note", "string", required: false));

    private const string ValidOrder = "{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2.5,\"express\":true}";

    [Theory]
    [InlineData(ValidOrder, null)]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2,\"express\":false,\"note\":\"hi\"}", null)]
    [InlineData("{\"quantity\":3,\"price\":2.5,\"express\":true}", "Required field 'customer-id' is missing.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2.5,\"express\":true,\"extra\":1}", "Field 'extra' is not declared by the shape.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":\"3\",\"price\":2.5,\"express\":true}", "Field 'quantity' must be an integer value.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3.5,\"price\":2.5,\"express\":true}", "Field 'quantity' must be an integer value.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":null,\"express\":true}", "Field 'price' must be a number value.")]
    [InlineData("{\"customer-id\":1,\"quantity\":3,\"price\":2.5,\"express\":true}", "Field 'customer-id' must be a string value.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2.5,\"express\":\"true\"}", "Field 'express' must be a boolean value.")]
    [InlineData("{\"customer-id\":\"c1\",\"customer-id\":\"c2\",\"quantity\":3,\"price\":2.5,\"express\":true}", "Field 'customer-id' appears more than once.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":1e999,\"express\":true}", "Field 'price' must be a number value.")]
    [InlineData("[]", "The value must be a JSON object.")]
    [InlineData("\"text\"", "The value must be a JSON object.")]
    [InlineData("{not json", "The value must be valid JSON with a maximum depth of 32.")]
    public void Boundary_validation_rejects_missing_extra_and_wrong_type_fields_with_a_clear_error(string json, string? expected) =>
        Assert.Equal(expected, JsonObjectShapes.ValidateValue(Order, json));

    [Fact]
    public void Boundary_validation_enforces_the_existing_json_size_and_depth_limits()
    {
        var oversized = "{\"customer-id\":\"" + new string('x', JsonObjectShapeLimits.MaximumPayloadCharacters) + "\"}";
        Assert.Contains("exceeds", JsonObjectShapes.ValidateValue(Order, oversized));
        var deep = string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40));
        Assert.Contains("maximum depth", JsonObjectShapes.ValidateValue(Order, "{\"customer-id\":" + deep + "}"));
    }

    [Fact]
    public void Shape_bounds_allow_thirty_two_fields_and_reject_thirty_three_zero_or_malformed_fields()
    {
        JsonObjectShape Many(int count) => new([.. Enumerable.Range(0, count).Select(index => Field($"f{index}", "string"))]);
        Assert.Null(JsonObjectShapes.Validate(Many(32)));
        Assert.NotNull(JsonObjectShapes.Validate(Many(33)));
        Assert.NotNull(JsonObjectShapes.Validate(Many(0)));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("a", "string"), Field("a", "integer"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("1bad", "string"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("has space", "string"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("a", "object"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("a", "array<string>"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("a", "json"))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(new JsonObjectFieldShape("a", " ", "string", true))));
        Assert.NotNull(JsonObjectShapes.Validate(Shape(Field("a", "string", name: new string('n', 129)))));
        Assert.NotNull(JsonObjectShapes.Validate(new JsonObjectShape(default)));
    }

    private static WorkflowDefinition Graph(JsonObjectShape? shape, string fieldId = "quantity", string fieldType = "integer",
        string outputType = "integer", string? inputType = "json")
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = inputType, InputShape = shape })
            .Add("get", new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new(fieldId, fieldType) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = outputType });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-get", new(new("input", "out"), new("get", "in"), "data"))
            .Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-output", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "shaped", "input", nodes, data) { ExecutionConnections = execution };
    }

    private static string[] Codes(WorkflowDefinition definition) => [.. WorkflowValidator.Validate(definition).Select(issue => issue.Code)];

    [Fact]
    public void A_shaped_input_feeding_object_get_validates_and_derives_the_typed_output_pin()
    {
        var graph = Graph(Order);
        Assert.Empty(WorkflowValidator.Validate(graph));
        var get = graph.Nodes["get"];
        Assert.Equal([new WorkflowPort("in", WorkflowDataTypes.Json)], WorkflowPorts.ExplicitDataInputs(get).ToArray());
        Assert.Equal([new WorkflowPort("out", WorkflowDataTypes.Integer)], WorkflowPorts.ExplicitDataOutputs(get).ToArray());
        Assert.Empty(WorkflowPorts.ExplicitExecutionInputs(get));
        Assert.True(WorkflowNodeContractCatalog.TryGet(NodeTypes.ObjectGet, out var contract));
        Assert.True(contract.IsPure);
        Assert.True(contract.HasDynamicPins);
        Assert.Equal(WorkflowDynamicPinIdentityKind.ObjectFieldType, contract.DynamicPinIdentity);
        Assert.Empty(WorkflowNodeContractConformance.ValidateCatalog());
        Assert.Equal("object-get", WorkflowNodeDescriptorResolver.Resolve(get).ConfigurationKind);
    }

    [Fact]
    public void Object_get_output_type_must_match_the_downstream_pin()
    {
        Assert.Contains("connection.port", Codes(Graph(Order, outputType: "string")));
    }

    [Theory]
    [InlineData("missing", "integer", "object.get.source")]
    [InlineData("quantity", "string", "object.get.source")]
    [InlineData("quantity", "json", "object.get.configuration")]
    [InlineData("not valid", "integer", "object.get.configuration")]
    public void Object_get_must_name_a_declared_field_with_the_declared_scalar_type(string fieldId, string type, string code) =>
        Assert.Contains(code, Codes(Graph(Order, fieldId, type, outputType: type is "json" ? "string" : type)));

    [Fact]
    public void Object_get_requires_a_shaped_input_source()
    {
        Assert.Contains("object.get.source", Codes(Graph(null)));
    }

    [Fact]
    public void A_shape_is_only_allowed_on_a_json_input_and_must_be_well_formed()
    {
        Assert.Contains("object.shape.unexpected", Codes(Graph(Order, inputType: null)));
        Assert.Contains("object.shape.unexpected", Codes(Graph(Order, inputType: "string")));
        Assert.Contains("object.shape", Codes(Graph(Shape(Field("a", "object")), "a", "string", "string")));
        var historical = Graph(Order) with { SchemaVersion = "1.13" };
        Assert.Contains("object.shape.unexpected", Codes(historical));
        Assert.Contains("node.schema", Codes(historical));
    }

    [Fact]
    public void Object_get_configuration_is_rejected_on_other_nodes()
    {
        var graph = Graph(Order);
        graph = graph with { Nodes = graph.Nodes.SetItem("output", graph.Nodes["output"] with { ObjectGet = new("quantity", "integer") }) };
        Assert.Contains("object.get.unexpected", Codes(graph));
    }

    [Fact]
    public void Renaming_a_field_display_name_does_not_change_wiring_or_validity()
    {
        var renamed = Shape([.. Order.Fields.Select(field => field.Id == "quantity" ? field with { Name = "Units ordered" } : field)]);
        var before = Graph(Order);
        var after = Graph(renamed);
        Assert.Empty(WorkflowValidator.Validate(after));
        Assert.Equal(before.Nodes["get"], after.Nodes["get"]);
        Assert.Equal(before.Connections, after.Connections);
        Assert.Equal(JsonObjectShapes.ValidateValue(Order, ValidOrder), JsonObjectShapes.ValidateValue(renamed, ValidOrder));
        Assert.True(JsonObjectShapes.TryReadField(renamed, ValidOrder, "quantity", out var value, out _));
        Assert.Equal(3L, value);
    }

    [Theory]
    [InlineData("customer-id", "c1")]
    [InlineData("quantity", 3L)]
    [InlineData("price", 2.5)]
    [InlineData("express", true)]
    public void Typed_field_reads_return_the_declared_clr_type(string fieldId, object expected)
    {
        Assert.True(JsonObjectShapes.TryReadField(Order, ValidOrder, fieldId, out var value, out var error), error);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Reading_an_optional_field_that_is_absent_fails_with_a_clear_error()
    {
        Assert.False(JsonObjectShapes.TryReadField(Order, ValidOrder, "note", out _, out var error));
        Assert.Equal("Field 'note' is absent from the object.", error);
    }

    [Fact]
    public void Shape_and_object_get_round_trip_and_unshaped_definitions_keep_their_bytes()
    {
        var graph = Graph(Order);
        var text = WorkflowJson.Serialize(graph);
        Assert.Equal(text, WorkflowJson.Serialize(WorkflowJson.Deserialize(text)));
        Assert.Contains("\"inputShape\"", text);
        Assert.Contains("\"objectGet\"", text);
        // Absent annotations are omitted, so every existing definition hash is unchanged.
        var plain = WorkflowJson.Serialize(WorkflowJson.Deserialize(
            """{"schemaVersion":"2.0","workflowId":"p","entryNodeId":"input","nodes":{"input":{"type":"core.input","typeVersion":"1.0.0"},"output":{"type":"core.output","typeVersion":"1.0.0"}},"connections":{"v":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}},"executionConnections":{"f":{"source":{"nodeId":"input","portId":"then"},"target":{"nodeId":"output","portId":"execute"},"kind":"execution"}}}"""));
        Assert.DoesNotContain("inputShape", plain);
        Assert.DoesNotContain("objectGet", plain);
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"type\":\"string\",\"required\":true,\"future\":1}")]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"type\":\"string\"}")]
    [InlineData("{\"id\":\"a\",\"type\":\"string\",\"required\":true}")]
    public void A_shape_the_reader_does_not_understand_is_rejected_on_read_never_downgraded(string field)
    {
        var json = """{"schemaVersion":"2.0","workflowId":"p","entryNodeId":"input","nodes":{"input":{"type":"core.input","typeVersion":"1.0.0","inputDataType":"json","inputShape":{"fields":[@FIELD@]}},"output":{"type":"core.output","typeVersion":"1.0.0","outputDataType":"json"}},"connections":{"v":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}},"executionConnections":{"f":{"source":{"nodeId":"input","portId":"then"},"target":{"nodeId":"output","portId":"execute"},"kind":"execution"}}}""".Replace("@FIELD@", field, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => WorkflowJson.Deserialize(json));
        Assert.ThrowsAny<Exception>(() => WorkflowJson.DeserializeDraft(json));
    }

    [Fact]
    public void A_shape_with_an_unsupported_field_type_is_rejected_by_deserialize()
    {
        var json = """{"schemaVersion":"2.0","workflowId":"p","entryNodeId":"input","nodes":{"input":{"type":"core.input","typeVersion":"1.0.0","inputDataType":"json","inputShape":{"fields":[{"id":"a","name":"A","type":"record<x@1>","required":true}]}},"output":{"type":"core.output","typeVersion":"1.0.0","outputDataType":"json"}},"connections":{"v":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}},"executionConnections":{"f":{"source":{"nodeId":"input","portId":"then"},"target":{"nodeId":"output","portId":"execute"},"kind":"execution"}}}""";
        var exception = Assert.ThrowsAny<Exception>(() => WorkflowJson.Deserialize(json));
        Assert.Contains("unsupported type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_object_get_stays_a_draftable_edit_but_is_never_publishable()
    {
        var graph = Graph(Order, "missing");
        Assert.NotEmpty(WorkflowValidator.Validate(graph));
        Assert.True(WorkflowJson.HasOnlyDraftableIssues(graph));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(graph));
        Assert.NotNull(WorkflowJson.SerializeDraft(graph));
    }
}

public sealed class ShapedJsonUntrustedTextTests
{
    private static readonly JsonObjectShape Shape = new([new("a", "A", "string", true)]);

    [Fact]
    public void Oversized_caller_key_names_are_truncated_in_errors()
    {
        var key = new string('k', 10_000);
        var undeclared = JsonObjectShapes.ValidateValue(Shape, "{\"a\":\"x\",\"" + key + "\":1}")!;
        var duplicate = JsonObjectShapes.ValidateValue(Shape, "{\"" + key + "\":1,\"" + key + "\":2}")!;
        Assert.True(undeclared.Length < 200, undeclared.Length.ToString());
        Assert.True(duplicate.Length < 200, duplicate.Length.ToString());
        Assert.Contains("…", undeclared, StringComparison.Ordinal);
    }

    [Fact]
    public void Lone_surrogate_escapes_in_a_value_or_a_key_do_not_throw()
    {
        var value = JsonObjectShapes.ValidateValue(Shape, "{\"a\":\"\\ud800\"}");
        Assert.True(value is null || value.Length < 200);
        var key = JsonObjectShapes.ValidateValue(Shape, "{\"a\":\"x\",\"\\ud800\":1}");
        Assert.NotNull(key);
        Assert.True(key.Length < 200);
        var read = JsonObjectShapes.TryReadField(Shape, "{\"a\":\"\\ud800\"}", "a", out _, out var error);
        Assert.True(read || error is not null);
    }
}
