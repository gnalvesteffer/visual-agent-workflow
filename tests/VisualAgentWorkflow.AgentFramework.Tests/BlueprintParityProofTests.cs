using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Crosses the authoring, serialized-definition, execution, and evidence seams
/// for the bounded Blueprint-inspired contracts. These are proof fixtures, not
/// a claim of unrestricted Blueprint compatibility.
/// </summary>
public sealed class BlueprintParityProofTests
{
    [Fact]
    public async Task Authored_pure_value_and_parameter_graph_survives_round_trip_and_records_data_evidence()
    {
        var authored = WorkflowEditing.InsertNodeAfter(
            ParameterBaseline(), "input",
            new NodeDefinition(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "answer" });
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(authored)), "proof");
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteWithParametersAsync(Guid.NewGuid(), revision, "ignored",
            new Dictionary<string, string> { ["answer"] = "authored value" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("authored value", output);
        var valueNodeId = authored.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowValue).Key;
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == valueNodeId && evt.Data == "authored value");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Authored_direct_mcp_graph_round_trips_and_records_tool_evidence()
    {
        var authored = DirectMcpDefinition();
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(authored)), "proof");
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "{\"query\":\"blueprint\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool result", result.Output);
        Assert.Equal("{\"query\":\"blueprint\"}", broker.Input);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool" && evt.Data == "tool result");
    }

    [Theory]
    [InlineData("say yes", "true", "SAY YES")]
    [InlineData("no", "false", "on")]
    public async Task Authored_conditional_graph_round_trips_and_records_only_the_selected_route(
        string input, string selectedPort, string expected)
    {
        var authored = ConditionalDefinition();
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(authored)), "proof");
        var events = new List<ExecutionEvent>();
        var conditionId = authored.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expected, result.Output);
        var decision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == conditionId);
        Assert.Equal(selectedPort, JsonDocument.Parse(decision.Data!).RootElement.GetProperty("port").GetString());
        Assert.DoesNotContain(events, evt => evt.NodeId == (selectedPort == "true" ? "false" : "true"));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Authored_pinned_child_workflow_round_trips_and_records_nested_scope_evidence()
    {
        var child = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("child"), "input", NodeTypes.Uppercase), "child-author");
        var parent = GraphEditing.InsertNodeAfter(LinearBaseline("parent"), "input", "out",
            new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
            });
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "proof");
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("HELLO", output);
        var callId = parent.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key;
        Assert.Contains(events, evt => evt.ScopeId == callId && evt.Kind == ExecutionEventKinds.SubworkflowStarted);
        Assert.Contains(events, evt => evt.ScopeId == callId && evt.Kind == ExecutionEventKinds.SubworkflowCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    private static WorkflowDefinition ParameterBaseline() => new(
        "1.6", "parameter-proof", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("edge", new(new("input", "out"), new("output", "in"), "data")))
    {
        Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            .Add("answer", new("Answer", "default"))
    };

    private static WorkflowDefinition LinearBaseline(string id) => new(
        "1.0", id, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("edge", new(new("input", "out"), new("output", "in"), "data")));

    private static WorkflowDefinition DirectMcpDefinition() => new(
        WorkflowSchemaVersions.DirectMcpGraph, "mcp-proof", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.echo", new string('a', 64))))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("tool-input", new(new("input", "out"), new("tool", "in"), "data"))
            .Add("output-value", new(new("tool", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate-tool", new(new("input", "then"), new("tool", "execute"), "execution"))
            .Add("complete", new(new("tool", "then"), new("output", "execute"), "execution"))
    };

    private static WorkflowDefinition ConditionalDefinition()
    {
        var baseline = LinearBaseline("conditional-proof");
        var graph = GraphEditing.InsertConditionalAfter(baseline, "input", "out");
        var condition = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        graph = GraphEditing.InsertNodeAfter(graph, condition, "true", new(NodeTypes.Uppercase, "1.0.0"));
        graph = GraphEditing.InsertNodeAfter(graph, condition, "false", new(NodeTypes.Reverse, "1.0.0"));
        return graph;
    }

    private sealed class RecordingBroker : INodeToolBroker
    {
        public string? Input { get; private set; }

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(RecordingBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.Input = input;
                return Task.FromResult("tool result");
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Input = arguments.GetRawText();
                return Task.FromResult("tool result");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
