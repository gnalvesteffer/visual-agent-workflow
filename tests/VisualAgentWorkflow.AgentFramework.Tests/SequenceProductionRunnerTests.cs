using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SequenceProductionRunnerTests
{
    [Fact]
    public async Task Production_runner_consumes_a_persisted_sequence_definition()
    {
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(PersistedSequenceDefinition(), "public-sequence");

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null,
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("hello", result.Output);
        Assert.Equal(["inventory", "architecture", "quality"], events
            .Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched)
            .Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched), eventItem =>
            Assert.Equal("sequence", ReadEvidence(eventItem).SequenceNodeId));
    }

    [Fact]
    public async Task Production_runner_records_ordered_sequence_lifecycle_through_normal_recorder()
    {
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(runId, "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.False(string.IsNullOrEmpty(result.Output));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceStarted));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted));
        var dispatched = events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched).ToArray();
        Assert.Equal([0, 1, 2], dispatched.Select(ReadEvidence).Select(evidence => evidence.Ordinal));
        Assert.Equal(["first", "second", "third"], dispatched.Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => SequenceEvidence.IsLifecycleEvent(eventItem.Kind)), eventItem =>
        {
            Assert.True(SequenceEvidence.TryRead(eventItem, out var evidence));
            Assert.NotNull(evidence);
            Assert.Equal(runId, eventItem.RunId);
        });
        Assert.Equal(Enumerable.Range(1, events.Count).Select(index => (long)index), events.Select(eventItem => eventItem.Sequence));
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.Transfer &&
            eventItem.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(["first", "second", "third"], playback.Outputs.Select(output => output.OutputId));
    }

    [Theory]
    [InlineData("Failed", ExecutionEventKinds.SequenceFailed, ExecutionEventKinds.SequenceOutputFailed)]
    [InlineData("Cancelled", ExecutionEventKinds.SequenceCancelled, ExecutionEventKinds.SequenceOutputCancelled)]
    public async Task Production_runner_preserves_distinct_sequence_failure_outcomes(
        string terminalStateName, string sequenceTerminalKind, string outputTerminalKind)
    {
        var events = new List<ExecutionEvent>();
        var terminalState = Enum.Parse<SequenceTerminalState>(terminalStateName);
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, "second", terminalState);

        Assert.True(result.Failed);
        Assert.Contains(events, eventItem => eventItem.Kind == outputTerminalKind);
        Assert.Contains(events, eventItem => eventItem.Kind == sequenceTerminalKind);
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Production_runner_cancellation_at_dispatch_boundary_does_not_admit_next_output()
    {
        var events = new List<ExecutionEvent>();
        var gate = new BoundaryGate("sequence:second");
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate,
            cancellationToken: cancellation.Token);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched &&
            ReadEvidence(eventItem).OutputId is "second" or "third");
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    [Fact]
    public async Task Production_runner_pause_at_dispatch_boundary_releases_once_without_redispatch()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence:second");
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        gate.Release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Failed);
        Assert.Equal(3, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    [Fact]
    public async Task Schema_1_43_executes_each_sequence_path_to_the_output_barrier()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequencePathDefinition(), "sequence-paths"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "inventory-tool");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "architecture-tool");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
    }

    [Fact]
    public async Task Schema_1_46_executes_a_persisted_terminal_agent_path_with_sequence_lineage()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor();
        var result = await new WorkflowRunner(new RecordingBroker(), agent).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("hello", result.Output);
        Assert.True(agent.InvocationObserved);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
    }

    [Fact]
    public async Task Schema_1_46_records_agent_failure_before_sequence_failure()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(fail: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(new RecordingBroker(), agent)
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path-failure"),
                "hello", null, evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed && ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Schema_1_46_records_agent_cancellation_before_sequence_cancellation()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(blockUntilCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(new RecordingBroker(), agent).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path-cancel"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled && ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Schema_1_43_records_failed_tool_path_and_sequence_failure_evidence()
    {
        var events = new List<ExecutionEvent>();
        var broker = new SequencePathBroker((_, _) => throw new InvalidOperationException("simulated tool failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequencePathDefinition(), "sequence-path-failure"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
            evt.NodeId is "inventory-tool" or "architecture-tool");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "failed");
        Assert.Contains(playback.Outputs, output => output.State == "cancelled");
    }

    [Fact]
    public async Task Schema_1_43_records_cancelled_tool_path_and_sequence_cancellation_evidence()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var broker = new SequencePathBroker(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        });
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequencePathDefinition(), "sequence-path-cancellation"), "ignored", null,
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
            evt.NodeId is "inventory-tool" or "architecture-tool");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("cancelled", playback!.Status);
        Assert.Equal("cancelled", Assert.Single(playback.Outputs, output => output.OutputId == "inventory").State);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_records_nested_agent_events_with_sequence_lineage()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor();
        var result = await new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("proof input", result.Output);
        Assert.True(agent.InvocationObserved);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
        Assert.Contains(events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested), evt =>
            evt.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.Contains(events.Where(evt => evt.Kind == ExecutionEventKinds.ModelResponded), evt =>
            evt.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_exercises_real_agent_executor_and_preserves_lineage()
    {
        var events = new List<ExecutionEvent>();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Proof", "scripted-text-v1", true),
            () => new DeterministicDemoChatClient());
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());
        var result = await new WorkflowRunner(agents: agents).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.StartsWith("Demo agent:", result.Output, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && HasSequenceLineage(evt));
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal("agent", Assert.Single(playback.Outputs).OutputId);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_fails_closed_when_provider_is_missing()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(
            agents: new AgentNodeExecutor([], new RecordingBroker())).ExecuteSequenceAgentProductionProofAsync(
                Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_records_provider_timeout_as_failure_without_retry()
    {
        var events = new List<ExecutionEvent>();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Blocking", "blocking-v1", false),
            () => new BlockingChatClient()) { InvocationTimeout = TimeSpan.FromMilliseconds(25) };
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agents)
            .ExecuteSequenceAgentProductionProofAsync(Guid.NewGuid(), "proof input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded &&
            evt.Data?.Contains("timeout", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
            evt.Data?.Contains("no automatic retry", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
    }

    [Fact]
    public async Task Private_agent_fanout_uses_authored_ordinal_for_result_when_responses_complete_out_of_order()
    {
        var events = new List<ExecutionEvent>();
        var client = new OutOfOrderChatClient();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Out of order", "out-of-order-v1", false), () => client);
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());
        var result = await new WorkflowRunner(agents: agents).ExecuteSequenceAgentFanoutProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("response-1", result.Output);
        Assert.True(client.SecondResponseCompletedFirst);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(2, playback.Outputs.Count);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_settles_output_when_agent_fails()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(fail: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_settles_output_when_agent_is_cancelled()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(blockUntilCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; },
            cancellationToken: cancellation.Token);

        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Private_agent_sequence_fanout_cancels_active_sibling_when_one_agent_fails()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(failFirstConcurrentInvocation: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent)
            .ExecuteSequenceAgentFanoutProductionProofAsync(Guid.NewGuid(), "proof input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "failing");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "sibling");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    private static SequenceLifecycleEvidence ReadEvidence(ExecutionEvent executionEvent)
    {
        Assert.True(SequenceEvidence.TryRead(executionEvent, out var evidence));
        return evidence!;
    }

    private static bool HasSequenceLineage(ExecutionEvent executionEvent) =>
        executionEvent.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true;

    private static WorkflowDefinition PersistedSequenceDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([
                    new("inventory", "Inventory"), new("architecture", "Architecture"), new("quality", "Quality")])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var edges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("output", "execute"), "execution"))
            .Add("sequence-quality", new(new("sequence", "then.quality"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceGraph, "sequence-runtime", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = edges
        };
    }

    private static WorkflowDefinition SequencePathDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("inventory", "Inventory"), new("architecture", "Architecture")])
            })
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.inventory", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.architecture", new string('b', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"))
            .Add("inventory-output", new(new("inventory-tool", "then"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("architecture-tool", "execute"), "execution"))
            .Add("architecture-output", new(new("architecture-tool", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "sequence-paths", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition SequenceAgentPathDefinition()
    {
        var source = SequencePathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
            { MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        return source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };
    }

    private sealed class SequenceProofAgentExecutor : IAgentNodeExecutor
    {
        private readonly bool fail;
        private readonly bool blockUntilCancellation;
        private readonly bool failFirstConcurrentInvocation;
        private int concurrentInvocationCount;

        public bool InvocationObserved { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> BothConcurrentInvocationsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SequenceProofAgentExecutor(bool fail = false, bool blockUntilCancellation = false,
            bool failFirstConcurrentInvocation = false)
        {
            this.fail = fail;
            this.blockUntilCancellation = blockUntilCancellation;
            this.failFirstConcurrentInvocation = failFirstConcurrentInvocation;
        }

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("sequence-proof", "Proof", "proof", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            InvocationObserved = true;
            Started.TrySetResult(true);
            var concurrentInvocationNumber = failFirstConcurrentInvocation
                ? Interlocked.Increment(ref concurrentInvocationCount) : 0;
            if (concurrentInvocationNumber == 2) BothConcurrentInvocationsStarted.TrySetResult(true);
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentStarted,
                JsonSerializer.Serialize(new { provider = new { modelId = "sequence-proof" } }));
            var modelCallId = Guid.NewGuid();
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelRequested,
                JsonSerializer.Serialize(new { modelCallId, input }));
            if (fail)
                throw new InvalidOperationException("proof agent failed");
            if (failFirstConcurrentInvocation)
            {
                await BothConcurrentInvocationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (concurrentInvocationNumber == 1)
                    throw new InvalidOperationException("proof agent failed");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (blockUntilCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelResponded,
                JsonSerializer.Serialize(new { modelCallId, output = input }));
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentCompleted, input);
            return input;
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Blocking client unexpectedly completed.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class OutOfOrderChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> secondResponded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;
        public bool SecondResponseCompletedFirst { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref requestCount);
            if (request == 2)
            {
                secondStarted.TrySetResult(true);
                secondResponded.TrySetResult(true);
                SecondResponseCompletedFirst = true;
                return Response("response-2");
            }

            await secondStarted.Task.WaitAsync(cancellationToken);
            await secondResponded.Task.WaitAsync(cancellationToken);
            await Task.Delay(20, cancellationToken);
            return Response("response-1");
        }

        private static ChatResponse Response(string text) => new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "out-of-order-v1",
            FinishReason = ChatFinishReason.Stop
        };

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingBroker : INodeToolBroker
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope();

        private sealed class Scope : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromResult("tool-result");

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
                Task.FromResult("tool-result");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BoundaryGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(WaitAsync(cancellationToken));
        }

        private static async Task WaitAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ReleaseGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(Release.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class SequencePathBroker(Func<string, CancellationToken, Task<string>> invoke) : INodeToolBroker
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invoke);

        private sealed class Scope(SequencePathBroker owner, Func<string, CancellationToken, Task<string>> invoke) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.Started.TrySetResult(true);
                return invoke(input, cancellationToken);
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Started.TrySetResult(true);
                return invoke(arguments.GetRawText(), cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
