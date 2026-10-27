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
