using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class DirectMcpExecutionTests
{
    [Fact]
    public async Task Direct_mcp_result_flows_to_the_output_data_pin()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "test"), "{\"text\":\"hello\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.Equal("{\"text\":\"hello\"}", broker.Input);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool" && evt.Data == "tool-result");
    }

    [Fact]
    public async Task Direct_mcp_requires_a_json_object_input()
    {
        var broker = new RecordingBroker();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "test"), "hello", null, _ => ValueTask.CompletedTask));
        Assert.Null(broker.Input);
    }

    [Fact]
    public async Task Configured_direct_mcp_arguments_override_the_connected_input()
    {
        var broker = new RecordingBroker();
        var baseline = Definition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("tool", baseline.Nodes["tool"] with { McpArgumentsJson = "{\"query\":\"configured\"}" })
        };

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"query\":\"connected\"}", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        Assert.Equal("{\"query\":\"configured\"}", broker.Input);
    }

    [Theory]
    [InlineData("1.37")]
    [InlineData("2.0")]
    public async Task Schema_1_37_builds_tool_arguments_from_typed_input_pins(string schemaVersion)
    {
        var broker = new RecordingBroker();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(PinDefinition() with { SchemaVersion = schemaVersion }, "test"), "ignored", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal("README.md", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal(50000, arguments.RootElement.GetProperty("maxBytes").GetInt64());
    }

    [Fact]
    public async Task Schema_1_40_emits_number_mcp_arguments_as_json_numbers()
    {
        var broker = new RecordingBroker();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberPinDefinition(), "test"), "ignored", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(JsonValueKind.Number, arguments.RootElement.GetProperty("amount").ValueKind);
        Assert.Equal(12.5d, arguments.RootElement.GetProperty("amount").GetDouble());
    }

    [Fact]
    public async Task Schema_1_41_resolves_a_number_parameter_into_a_persisted_mcp_pin()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberParameterPinDefinition(), "test"), "ignored",
            new Dictionary<string, string> { ["amount"] = "-0" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(JsonValueKind.Number, arguments.RootElement.GetProperty("amount").ValueKind);
        Assert.True(double.IsNegative(arguments.RootElement.GetProperty("amount").GetDouble()));
        var evidence = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "amount");
        Assert.Contains("\"dataType\":\"number\"", evidence.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_1_42_evaluates_number_addition_before_building_mcp_arguments()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberAdditionPinDefinition(), "test"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(5.75d, arguments.RootElement.GetProperty("amount").GetDouble());
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "sum" && evt.Data?.Contains("5.75", StringComparison.Ordinal) == true);
    }

    private static WorkflowDefinition Definition() => new(
        WorkflowSchemaVersions.DirectMcpGraph, "direct-mcp-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.echo", new string('a', 64))),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["tool-input"] = new(new("input", "out"), new("tool", "in"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    [Fact]
    public async Task Unified_approval_captures_typed_arguments_and_dispatches_the_approved_snapshot()
    {
        var baseline = PinDefinition();
        var definition = baseline with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Nodes = baseline.Nodes.Add("approval", new(NodeTypes.Approval, "1.0.0") { Approval = new("Approve read") }),
            Connections = baseline.Connections.Add("approval-input", new(new("input", "out"), new("approval", "in"), "data")),
            ExecutionConnections = baseline.ExecutionConnections!
                .SetItem("activate-tool", new(new("input", "then"), new("approval", "execute"), "execution"))
                .Add("approved-tool", new(new("approval", "then"), new("tool", "execute"), "execution"))
        };
        var broker = new RecordingBroker();
        var gate = new CapturingApprovalGate();
        var result = await new WorkflowRunner(broker).ExecuteControlledWithParametersAndOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "caller data", null, _ => ValueTask.CompletedTask, gate);

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.NotNull(gate.Action);
        Assert.Equal(gate.Action.Input, broker.Input);
        using var arguments = JsonDocument.Parse(gate.Action.Input);
        Assert.Equal("README.md", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal(50000, arguments.RootElement.GetProperty("maxBytes").GetInt64());
    }

    private sealed class CapturingApprovalGate : IWorkflowExecutionGate, IWorkflowApprovalGate
    {
        public WorkflowApprovalAction? Action { get; private set; }
        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<WorkflowApprovalPermit> WaitForApprovalAsync(WorkflowApprovalAction action, string prompt,
            CancellationToken cancellationToken)
        {
            Action = action;
            return ValueTask.FromResult(new WorkflowApprovalPermit(Guid.NewGuid(), action.ComputeHash(), action.TargetInvocationId));
        }
    }

    private static WorkflowDefinition PinDefinition() => new(
        WorkflowSchemaVersions.DirectMcpPinsGraph, "direct-mcp-pins-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["path"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("README.md") },
            ["max"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(50000) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.read", new string('a', 64)))
            {
                McpInputPorts = [new("arg.path", "path", "string", true), new("arg.maxBytes", "maxBytes", "integer", false)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["path"] = new(new("path", "out"), new("tool", "arg.path"), "data"),
            ["max"] = new(new("max", "out"), new("tool", "arg.maxBytes"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition NumberPinDefinition() => new(
        WorkflowSchemaVersions.NumberGraph, "direct-mcp-number-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["amount"] = new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(12.5d) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.number", new string('a', 64)))
            {
                McpInputPorts = [new("arg.amount", "amount", WorkflowDataTypes.Number, true)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["amount"] = new(new("amount", "out"), new("tool", "arg.amount"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition NumberParameterPinDefinition() => NumberPinDefinition() with
    {
        SchemaVersion = WorkflowSchemaVersions.NumberPropagationGraph,
        Nodes = NumberPinDefinition().Nodes.SetItem("amount", new(NodeTypes.WorkflowValue, "1.0.0")
        {
            ParameterId = "amount", OutputDataType = WorkflowDataTypes.Number
        }),
        Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            .Add("amount", new WorkflowParameterDefinition("Amount", "12.5") { Type = "number" })
    };

    private static WorkflowDefinition NumberAdditionPinDefinition()
    {
        var baseline = NumberPinDefinition() with { SchemaVersion = WorkflowSchemaVersions.NumberArithmeticGraph };
        return baseline with
        {
            Nodes = baseline.Nodes
                .Remove("amount")
                .Add("left", new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(2.5d) })
                .Add("right", new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(3.25d) })
                .Add("sum", new(NodeTypes.NumberAdd, "1.0.0")),
            Connections = baseline.Connections
                .Remove("amount")
                .Add("left", new(new("left", "out"), new("sum", "left"), "data"))
                .Add("right", new(new("right", "out"), new("sum", "right"), "data"))
                .Add("amount", new(new("sum", "out"), new("tool", "arg.amount"), "data"))
        };
    }

    private sealed class RecordingBroker : INodeToolBroker
    {
        public string? Input { get; private set; }

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new RecordingScope(this);

        private sealed class RecordingScope(RecordingBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.Input = input;
                return Task.FromResult("tool-result");
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Input = arguments.GetRawText();
                return Task.FromResult("tool-result");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
