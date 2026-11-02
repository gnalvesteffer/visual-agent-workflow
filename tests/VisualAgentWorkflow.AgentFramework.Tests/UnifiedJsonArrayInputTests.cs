using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class UnifiedJsonArrayInputTests
{
    [Theory]
    [InlineData("[]", "0")]
    [InlineData("[\"a\",1,null]", "3")]
    public async Task Unified_input_array_chain_counts_items_and_records_data_evaluation(string input, string expected)
    {
        var definition = ArrayInputGraph();
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(expected, result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "to-array");
        var length = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "length");
        using var evidence = JsonDocument.Parse(length.Data!);
        Assert.Equal(WorkflowDataTypes.Integer, evidence.RootElement.GetProperty("dataType").GetString());
        Assert.Equal(expected, evidence.RootElement.GetProperty("value").GetRawText());
    }

    [Fact]
    public async Task Unified_input_array_chain_accepts_the_maximum_item_count()
    {
        var input = "[" + string.Join(',', Enumerable.Range(0, WorkflowJsonArrayLimits.MaximumItems)) + "]";
        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayInputGraph(), "test"), input, null, _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        Assert.Equal(WorkflowJsonArrayLimits.MaximumItems.ToString(), result.Output);
    }

    [Fact]
    public async Task Unified_input_array_chain_enforces_item_bound()
    {
        var input = "[" + string.Join(',', Enumerable.Range(0, WorkflowJsonArrayLimits.MaximumItems + 1)) + "]";
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayInputGraph(), "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "to-array");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "output");
    }

    [Fact]
    public async Task Unified_input_array_chain_rejects_non_array_json()
    {
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayInputGraph(), "test"), "{\"items\":[]}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "to-array");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "output");
    }

    [Theory]
    [InlineData("[\"first\",{\"ok\":true},null]", 0, "\"first\"")]
    [InlineData("[\"first\",{\"ok\":true},null]", 1, "{\"ok\":true}")]
    [InlineData("[\"first\",{\"ok\":true},null]", 2, "null")]
    public async Task Unified_array_get_returns_json_value_and_records_typed_evidence(string input, long index, string expected)
    {
        var graph = ArrayGetGraph(index);
        WorkflowValidator.ThrowIfInvalid(graph);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(graph)), "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(expected, result.Output);
        var item = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "get");
        using var evidence = JsonDocument.Parse(item.Data!);
        Assert.Equal(WorkflowDataTypes.Json, evidence.RootElement.GetProperty("dataType").GetString());
        Assert.Equal(expected, evidence.RootElement.GetProperty("value").GetRawText());
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[1]", -1)]
    [InlineData("[1]", 1)]
    [InlineData("[1]", 99)]
    public async Task Unified_array_get_fails_for_negative_or_out_of_range_indices(string input, long index)
    {
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(ArrayGetGraph(index), "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "get");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "output");
    }

    private static WorkflowDefinition ArrayInputGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
            ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-array"] = new(new("input", "out"), new("to-array", "in"), "data"),
            ["array-length"] = new(new("to-array", "out"), new("length", "in"), "data"),
            ["length-output"] = new(new("length", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-json-array-input", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition ArrayGetGraph(long index)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
            ["index"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(index) },
            ["get"] = new(NodeTypes.ArrayGet, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-array"] = new(new("input", "out"), new("to-array", "in"), "data"),
            ["array-get"] = new(new("to-array", "out"), new("get", "array"), "data"),
            ["index-get"] = new(new("index", "out"), new("get", "index"), "data"),
            ["get-output"] = new(new("get", "item"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-json-array-get", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }
}
