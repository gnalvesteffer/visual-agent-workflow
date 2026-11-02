using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class WorkflowTypedArrayTests
{
    [Theory]
    [InlineData("string", "[\"value\"]", "\"value\"", "value")]
    [InlineData("boolean", "[true]", "true", "true")]
    [InlineData("integer", "[42]", "42", "42")]
    [InlineData("number", "[1.5]", "1.5", "1.5")]
    public async Task Array_get_returns_each_supported_primitive_type(string type, string values, string evidenceValue, string output)
    {
        var graph = PrimitiveGetGraph(type, values);
        WorkflowValidator.ThrowIfInvalid(graph);
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "typed-array"), "input", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal(output, result.Output);
        AssertDataEvidence(events, "get", type, evidenceValue);
    }

    [Theory]
    [InlineData("string", "[\"alpha\",\"β\"]")]
    [InlineData("boolean", "[true,false]")]
    [InlineData("integer", "[-1,0,9223372036854775807]")]
    [InlineData("number", "[1.5,-2e3]")]
    [InlineData("string", "[]")]
    public async Task Typed_array_workflow_input_and_output_preserve_json_values(string type, string input)
    {
        var original = ArrayBoundaryGraph(type);
        var graph = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(original, WorkflowLayout.Create(original))).Definition;
        WorkflowValidator.ThrowIfInvalid(graph);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(graph, "typed-array-boundary"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(input, result.Output);
        var inputEvidence = events.First(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "input");
        using var evidence = JsonDocument.Parse(inputEvidence.Data!);
        Assert.Equal($"array<{type}>", evidence.RootElement.GetProperty("dataType").GetString());
        using var expected = JsonDocument.Parse(input);
        var expectedItems = expected.RootElement.EnumerateArray().ToArray();
        var actualItems = evidence.RootElement.GetProperty("value").EnumerateArray().ToArray();
        Assert.Equal(expectedItems.Length, actualItems.Length);
        for (var index = 0; index < expectedItems.Length; index++)
        {
            switch (type)
            {
                case "string": Assert.Equal(expectedItems[index].GetString(), actualItems[index].GetString()); break;
                case "boolean": Assert.Equal(expectedItems[index].GetBoolean(), actualItems[index].GetBoolean()); break;
                case "integer": Assert.Equal(expectedItems[index].GetInt64(), actualItems[index].GetInt64()); break;
                case "number": Assert.Equal(expectedItems[index].GetDouble(), actualItems[index].GetDouble()); break;
            }
        }
    }

    [Theory]
    [InlineData("string", "[1]")]
    [InlineData("boolean", "[null]")]
    [InlineData("integer", "[1.5]")]
    [InlineData("number", "[1e999]")]
    [InlineData("string", "{\"not\":\"an array\"}")]
    public async Task Typed_array_workflow_input_rejects_invalid_values(string type, string input)
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayBoundaryGraph(type), "typed-array-boundary"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
    }

    [Fact]
    public async Task Typed_array_input_rejects_utf8_payloads_over_the_shared_bound()
    {
        var input = "[\"" + new string('界', 5_500) + "\"]";
        Assert.True(input.Length < 16_384);
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayBoundaryGraph("string"), "typed-array-boundary"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
    }

    [Fact]
    public async Task Typed_array_literals_compose_with_get_length_and_scalar_nodes_with_evidence()
    {
        var definition = Graph("[\"x\"]", 0);
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "typed-array"),
            "ignored activation input", null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("x1", result.Output);
        AssertDataEvidence(events, "literal", "array<string>", "[\"x\"]");
        AssertDataEvidence(events, "get", "string", "\"x\"");
        AssertDataEvidence(events, "length", "integer", "1");
    }

    [Fact]
    public async Task Typed_array_index_failure_is_attributed_to_array_get()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Graph("[\"x\"]", 4), "typed-array"), "input", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "get");
    }

    private static void AssertDataEvidence(IEnumerable<ExecutionEvent> events, string nodeId, string dataType, string rawValue)
    {
        var item = events.First(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == nodeId);
        using var evidence = JsonDocument.Parse(item.Data!);
        Assert.Equal(dataType, evidence.RootElement.GetProperty("dataType").GetString());
        Assert.Equal(rawValue, evidence.RootElement.GetProperty("value").GetRawText());
    }

    private static WorkflowDefinition Graph(string values, long index)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["literal"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, values) },
            ["index"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(index) },
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
            ["literal-length"] = new(new("literal", "out"), new("length", "in"), "data"),
            ["get-concat"] = new(new("get", "item"), new("concat", "left"), "data"),
            ["length-text"] = new(new("length", "out"), new("length-text", "in"), "data"),
            ["text-concat"] = new(new("length-text", "out"), new("concat", "right"), "data"),
            ["concat-output"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-array-runtime", "input", nodes, data)
        { ExecutionConnections = execution };
    }

    private static WorkflowDefinition PrimitiveGetGraph(string type, string values)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["literal"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(type, values) },
            ["index"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(0) },
            ["get"] = new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = type },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = type }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["literal-get"] = new(new("literal", "out"), new("get", "array"), "data"),
            ["index-get"] = new(new("index", "out"), new("get", "index"), "data"),
            ["get-output"] = new(new("get", "item"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-array-primitive", "input", nodes, data)
        { ExecutionConnections = execution };
    }

    private static WorkflowDefinition ArrayBoundaryGraph(string elementType)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.ArrayOf(elementType) },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.ArrayOf(elementType) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-output-value", new(new("input", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-output-execution", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-array-boundary", "input", nodes, data)
        { ExecutionConnections = execution };
    }
}
