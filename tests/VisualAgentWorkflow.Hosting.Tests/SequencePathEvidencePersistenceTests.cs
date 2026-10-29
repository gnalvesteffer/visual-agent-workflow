using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class SequencePathEvidencePersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-sequence-evidence-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_43_success_survives_runtime_shutdown_and_store_reopen(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(Definition(), "sequence-persistence");
        var broker = new SequenceBroker((_, _) => Task.FromResult("tool-result"));
        var runId = await ExecuteThroughRuntime(store, revision, broker);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("completed", reopened.Status);
        Assert.True(SequenceEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(["inventory", "architecture"], playback.Outputs.Select(output => output.OutputId));
        Assert.All(playback.Outputs, output => Assert.Equal("completed", output.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_43_failure_survives_runtime_shutdown_and_replays_without_tool_calls(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(Definition(), "sequence-persistence-failure");
        var broker = new SequenceBroker((_, _) => throw new InvalidOperationException("simulated failure"));
        var runId = await ExecuteThroughRuntime(store, revision, broker);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("failed", reopened.Status);
        Assert.True(SequenceEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "failed");
        Assert.Contains(playback.Outputs, output => output.State == "cancelled");
        Assert.InRange(broker.InvocationCount, 1, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_43_cancellation_survives_runtime_shutdown_and_replays_skips(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(Definition(), "sequence-persistence-cancel");
        var broker = new SequenceBroker(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        });
        var runId = await ExecuteCancelledThroughRuntime(store, revision, broker);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("cancelled", reopened.Status);
        Assert.True(SequenceEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal("cancelled", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "cancelled");
        Assert.All(playback.Outputs, output => Assert.True(output.State is "cancelled" or "skipped"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_46_agent_lineage_survives_reopen_and_passive_playback(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(AgentDefinition(), "sequence-agent-persistence");
        var agent = new SequenceAgentExecutor();
        var broker = new SequenceBroker((_, _) => Task.FromResult("tool-result"));
        var runId = await ExecuteThroughRuntime(store, revision, broker, agent);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("completed", reopened.Status);
        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal("completed", playback!.Sequence.Status);
        var agentPlayback = Assert.Single(playback.Agents);
        Assert.Equal("completed", agentPlayback.State);
        Assert.Equal("inventory", agentPlayback.OutputId);
        Assert.Equal("completed", Assert.Single(playback.ModelCalls).State);
        Assert.Equal(1, agent.InvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_46_agent_failure_survives_reopen_and_passive_playback(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(AgentDefinition(), "sequence-agent-failure-persistence");
        var agent = new SequenceAgentExecutor(AgentOutcome.Failure);
        var runId = await ExecuteThroughRuntime(store, revision, new SequenceBroker((_, _) => Task.FromResult("unused")), agent);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("failed", reopened.Status);
        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        var agentPlayback = Assert.Single(playback!.Agents);
        Assert.Equal("failed", agentPlayback.State);
        Assert.Equal("running", Assert.Single(playback.ModelCalls).State);
        Assert.Equal("failed", playback.Sequence.Status);
        Assert.Equal(1, agent.InvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_1_46_agent_cancellation_survives_reopen_and_passive_playback(bool sqlite)
    {
        var store = CreateStore(sqlite);
        var revision = WorkflowRevision.Create(AgentDefinition(), "sequence-agent-cancel-persistence");
        var agent = new SequenceAgentExecutor(AgentOutcome.Cancelled);
        var runId = await ExecuteCancelledThroughRuntime(store, revision,
            new SequenceBroker((_, _) => Task.FromResult("unused")), agent);

        var reopened = CreateStore(sqlite).Get(runId)!;
        Assert.Equal("cancelled", reopened.Status);
        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        var agentPlayback = Assert.Single(playback!.Agents);
        Assert.Equal("cancelled", agentPlayback.State);
        Assert.Equal("running", Assert.Single(playback.ModelCalls).State);
        Assert.Equal("cancelled", playback.Sequence.Status);
        Assert.Equal(1, agent.InvocationCount);
    }

    private async Task<Guid> ExecuteThroughRuntime(IWorkflowRunStore store, WorkflowRevision revision, SequenceBroker broker,
        IAgentNodeExecutor? agents = null)
    {
        using var runtime = new LocalWorkflowRuntime(store, NullLogger<LocalWorkflowRuntime>.Instance, broker, agents);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var queued = await runtime.StartRevisionAsync(revision, "research input");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunSnapshot snapshot;
            do
            {
                await Task.Delay(10, deadline.Token);
                snapshot = (await runtime.GetRunAsync(queued.Header.RunId))!;
            } while (!snapshot.IsTerminal);

            return snapshot.Header.RunId;
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
        }
    }

    private async Task<Guid> ExecuteCancelledThroughRuntime(IWorkflowRunStore store, WorkflowRevision revision, SequenceBroker broker,
        IAgentNodeExecutor? agents = null)
    {
        using var runtime = new LocalWorkflowRuntime(store, NullLogger<LocalWorkflowRuntime>.Instance, broker, agents);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var queued = await runtime.StartRevisionAsync(revision, "research input");
            if (agents is SequenceAgentExecutor agent)
                await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            else
                await broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.CancelRunAsync(queued.Header.RunId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunSnapshot snapshot;
            do
            {
                await Task.Delay(10, deadline.Token);
                snapshot = (await runtime.GetRunAsync(queued.Header.RunId))!;
            } while (!snapshot.IsTerminal);

            return snapshot.Header.RunId;
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
        }
    }

    private IWorkflowRunStore CreateStore(bool sqlite) => sqlite
        ? new SqliteWorkflowRunStore(Database)
        : new FileRunJournal(directory);

    private static WorkflowDefinition Definition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("inventory", "Inventory"), new("architecture", "Architecture")])
            })
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0", new("demo.inventory", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0", new("demo.architecture", new string('b', 64)))
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
        return new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "sequence-persistence", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition AgentDefinition()
    {
        var definition = Definition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
            { MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = definition.Nodes.Remove("inventory-tool").Add("research-agent", agent),
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("research-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("research-agent", "out"), new("output", "execute"), "execution"))
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class SequenceBroker(Func<string, CancellationToken, Task<string>> invoke) : INodeToolBroker
    {
        public int InvocationCount { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invoke);

        private sealed class Scope(SequenceBroker owner, Func<string, CancellationToken, Task<string>> invoke) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.InvocationCount++;
                owner.Started.TrySetResult(true);
                return invoke(input, cancellationToken);
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.InvocationCount++;
                owner.Started.TrySetResult(true);
                return invoke(arguments.GetRawText(), cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceAgentExecutor(AgentOutcome outcome = AgentOutcome.Success) : IAgentNodeExecutor
    {
        public int InvocationCount { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("sequence-proof", "Proof", "proof", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            Started.TrySetResult(true);
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentStarted,
                JsonSerializer.Serialize(new { provider = new { modelId = "sequence-proof" } }));
            var modelCallId = Guid.NewGuid();
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelRequested,
                JsonSerializer.Serialize(new { modelCallId, input }));
            if (outcome is AgentOutcome.Failure or AgentOutcome.Cancelled)
            {
                await invocation.RecordEventAsync(ExecutionEventKinds.AgentFailed,
                    JsonSerializer.Serialize(new { outcome = outcome is AgentOutcome.Cancelled ? "cancelled" : "provider_error" }));
                if (outcome == AgentOutcome.Cancelled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return "unreachable";
                }
                throw new InvalidOperationException("simulated agent failure");
            }
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelResponded,
                JsonSerializer.Serialize(new { modelCallId, output = input }));
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentCompleted,
                JsonSerializer.Serialize(new { output = input }));
            return input;
        }
    }

    private enum AgentOutcome
    {
        Success,
        Failure,
        Cancelled
    }
}
