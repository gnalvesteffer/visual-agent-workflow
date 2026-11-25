using System.Collections.Immutable;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Runtime behavior of the shaped json first slice (Decision 0082, P5): boundary rejection and typed field reads.</summary>
public sealed class ShapedJsonObjectRuntimeTests
{
    private static readonly JsonObjectShape Order = new([
        new("customer-id", "Customer", "string", true), new("quantity", "Units", "integer", true),
        new("price", "Price", "number", true), new("express", "Express", "boolean", true), new("note", "Note", "string", false)]);

    private static WorkflowDefinition Graph(string fieldId, string type, JsonObjectShape? shape = null)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = "json", InputShape = shape ?? Order })
            .Add("get", new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new(fieldId, type) })
            .Add("text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = type == "integer" ? "string" : type });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-get", new(new("input", "out"), new("get", "in"), "data"));
        data = type == "integer"
            ? data.Add("get-text", new(new("get", "out"), new("text", "in"), "data")).Add("text-output", new(new("text", "out"), new("output", "in"), "data"))
            : data.Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        if (type != "integer") nodes = nodes.Remove("text");
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-output", new(new("input", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "shaped-runtime", "input", nodes, data) { ExecutionConnections = execution };
    }

    private static Task<WorkflowExecutionResult> Execute(WorkflowDefinition graph, string input, List<ExecutionEvent> events)
    {
        WorkflowValidator.ThrowIfInvalid(graph);
        return new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(graph)), "shaped"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
    }

    private static async Task<(WorkflowExecutionResult Result, List<ExecutionEvent> Events)> Run(WorkflowDefinition graph, string input)
    {
        var events = new List<ExecutionEvent>();
        return (await Execute(graph, input, events), events);
    }

    private static async Task<List<ExecutionEvent>> RunExpectingFailure(WorkflowDefinition graph, string input)
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Execute(graph, input, events));
        return events;
    }

    private const string Valid = "{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2.5,\"express\":true}";

    [Theory]
    [InlineData("customer-id", "string", "c1")]
    [InlineData("quantity", "integer", "3")]
    [InlineData("price", "number", "2.5")]
    [InlineData("express", "boolean", "true")]
    public async Task Object_get_returns_each_scalar_type_by_field_id_and_records_typed_evidence(string fieldId, string type, string output)
    {
        var (result, events) = await Run(Graph(fieldId, type), Valid);
        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(output, result.Output, ignoreCase: true);
        Assert.Contains(events, evt => evt.NodeId == "get" && evt.Data?.Contains($"\"{type}\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Renaming_a_display_name_changes_neither_the_wiring_nor_the_result()
    {
        var renamed = new JsonObjectShape([.. Order.Fields.Select(field => field.Id == "quantity" ? field with { Name = "Renamed" } : field)]);
        var (result, _) = await Run(Graph("quantity", "integer", renamed), Valid);
        Assert.False(result.Failed);
        Assert.Equal("3", result.Output);
    }

    [Theory]
    [InlineData("{\"quantity\":3,\"price\":2.5,\"express\":true}", "Required field 'customer-id' is missing.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":3,\"price\":2.5,\"express\":true,\"extra\":1}", "Field 'extra' is not declared by the shape.")]
    [InlineData("{\"customer-id\":\"c1\",\"quantity\":\"3\",\"price\":2.5,\"express\":true}", "Field 'quantity' must be an integer value.")]
    public async Task Invalid_run_input_is_rejected_at_the_boundary_with_a_clear_error(string input, string problem)
    {
        var events = await RunExpectingFailure(Graph("quantity", "integer"), input);
        var failure = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "input");
        Assert.Contains(problem, failure.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "get");
    }

    [Fact]
    public async Task Object_get_on_an_absent_optional_field_fails_the_node_with_a_clear_error()
    {
        var events = await RunExpectingFailure(Graph("note", "string"), Valid);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
            evt.Data?.Contains("Field 'note' is absent from the object.", StringComparison.Ordinal) == true);
    }
}
