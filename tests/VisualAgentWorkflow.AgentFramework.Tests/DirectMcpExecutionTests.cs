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

    [Fact]
    public async Task Schema_1_37_builds_tool_arguments_from_typed_input_pins()
    {
        var broker = new RecordingBroker();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(PinDefinition(), "test"), "ignored", null,
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
