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
}
