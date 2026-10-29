using System.Collections.Immutable;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public sealed class SequenceMcpEvidenceTests
{
    [Fact]
    public async Task Persisted_sequence_records_real_mcp_denial_as_a_failed_output()
    {
        var registrations = BrokerTests.Registrations()
            .Where(registration => registration.BindingId is "demo.uppercase" or "demo.reverse")
            .ToArray();
        var broker = new McpToolBroker(registrations, new DenyBindingPolicy("demo.reverse"));
        var bindings = await broker.ListBindingsAsync();
        var events = new List<ExecutionEvent>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(Definition(bindings[0], bindings[1]), "sequence-mcp-denied"),
            "ignored", null, executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }));
        Assert.IsType<ToolAccessDeniedException>(failure.InnerException);

        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.ToolDenied);
        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "failed");
    }

    [Fact]
    public async Task Persisted_sequence_records_real_mcp_post_dispatch_timeout_as_unknown()
    {
        var registrations = BrokerTests.Registrations()
            .Where(item => item.BindingId is "demo.slow" or "demo.uppercase")
            .ToArray();
        var discoveryBroker = new McpToolBroker(registrations, new DemoToolPolicy());
        var bindings = await discoveryBroker.ListBindingsAsync();
        var broker = new McpToolBroker(registrations, new DemoToolPolicy(), TimeSpan.FromMilliseconds(500));
        var events = new List<ExecutionEvent>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(Definition(bindings[0], bindings[1]), "sequence-mcp-timeout"),
            "ignored", null, executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }));
        Assert.IsType<ToolInvocationException>(failure.InnerException);

        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.ToolUncertain);
        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, executionEvent => executionEvent.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "failed");
    }

    private static WorkflowDefinition Definition(ToolBindingDescriptor first, ToolBindingDescriptor? second)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration(second is null
                    ? [new("first", "First")]
                    : [new("first", "First"), new("second", "Second")])
            })
            .Add("first-tool", Tool(first))
            .Add("output", new(NodeTypes.Output, "1.0.0"));

        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-first", new(new("sequence", "then.first"), new("first-tool", "execute"), "execution"))
            .Add("first-output", new(new("first-tool", "then"), new("output", "execute"), "execution"));

        if (second is not null)
        {
            nodes = nodes.Add("second-tool", Tool(second));
            execution = execution
                .Add("sequence-second", new(new("sequence", "then.second"), new("second-tool", "execute"), "execution"))
                .Add("second-output", new(new("second-tool", "then"), new("output", "execute"), "execution"));
        }

        return new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "sequence-mcp-evidence", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static NodeDefinition Tool(ToolBindingDescriptor binding) =>
        new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest(binding.BindingId, binding.ContractHash)
        {
            DescriptionHash = binding.DescriptionHash
        }) { McpArgumentsJson = "{}" };

    private sealed class DenyBindingPolicy(string deniedBinding) : IToolAuthorizationPolicy
    {
        public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) => bindingId != deniedBinding;
    }
}
