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
}
