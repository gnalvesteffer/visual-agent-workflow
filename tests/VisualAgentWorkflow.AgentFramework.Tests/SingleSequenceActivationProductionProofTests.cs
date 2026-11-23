using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SingleSequenceActivationProductionProofTests
{
    [Fact]
    public async Task One_framed_item_uses_production_sequence_dispatch_and_root_lifecycle()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "single-sequence-activation-proof", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("body", "Body"), new("follow-up", "Follow-up")])
                })
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("follow-up-agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "out", "agent", "in", "data"))
                .Add("input-follow-up-agent", Edge("input", "out", "follow-up-agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", Edge("input", "then", "sequence", "execute"))
                .Add("sequence-agent", Edge("sequence", "then.body", "agent", "execute"))
                .Add("sequence-follow-up-agent", Edge("sequence", "then.follow-up", "follow-up-agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
                .Add("follow-up-output", Edge("follow-up-agent", "out", "output", "execute"))
        };
        var revision = WorkflowRevision.Create(definition, "single-sequence-activation-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteSingleSequenceActivationProductionProofAsync(runId, revision, "one-item", 0, "index",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("one-item", result.Output);
        Assert.Equal(new[] { "one-item", "one-item" },
            agent.Invocations.Select(invocation => invocation.Input));
        Assert.All(agent.Invocations, invocation =>
            Assert.Equal("0", invocation.Parameters!["index"]));

        var bodyStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "agent");
        var bodyCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "agent");
        var sequenceSettled = events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted).ToArray();
        Assert.Equal(2, sequenceSettled.Length);
        var sequenceCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        var rootStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Started && evt.NodeId is null);
        var rootCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        Assert.Equal("array-loop/0", bodyStarted.ScopeId);
        Assert.Equal("array-loop/0", bodyCompleted.ScopeId);
        Assert.All(sequenceSettled, evt => Assert.Equal("array-loop/0", evt.ScopeId));
        Assert.Equal("array-loop/0", sequenceCompleted.ScopeId);
        Assert.True(rootStarted.Sequence < bodyCompleted.Sequence);
        Assert.True(bodyCompleted.Sequence < sequenceSettled[^1].Sequence);
        Assert.True(sequenceSettled[^1].Sequence < sequenceCompleted.Sequence);
        Assert.True(sequenceCompleted.Sequence < rootCompleted.Sequence);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Multi_item_coordinator_settles_each_production_sequence_before_admitting_the_next()
    {
        var revision = CreateSequenceRevision("multi-item-sequence-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("gamma", result.Output);
        Assert.Equal(new[] { "alpha", "alpha", "beta", "beta", "gamma", "gamma" },
            agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "0", "0", "1", "1", "2", "2" }, agent.Invocations
            .Select(invocation => invocation.Parameters!["index"]));
        Assert.Equal(new[] { "alpha", "alpha", "beta", "beta", "gamma", "gamma" }, agent.Invocations
            .Select(invocation => invocation.Parameters!["item"]));
        Assert.Equal(new[] { "agent", "follow-up-agent", "agent", "follow-up-agent", "agent", "follow-up-agent" },
            agent.Invocations.Select(invocation => invocation.NodeId));

        var iterationStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        var iterationCompletions = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).ToArray();
        var sequenceSettlements = events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceCompleted).ToArray();
        Assert.Equal(3, iterationStarts.Length);
        Assert.Equal(3, iterationCompletions.Length);
        Assert.Equal(3, sequenceSettlements.Length);
        for (var index = 0; index < iterationStarts.Length; index++)
        {
            var scope = $"array-loop/{index}";
            Assert.Equal(scope, iterationStarts[index].ScopeId);
            Assert.Equal(scope, iterationCompletions[index].ScopeId);
            Assert.Equal(scope, sequenceSettlements[index].ScopeId);
            Assert.Equal("completed", JsonDocument.Parse(iterationCompletions[index].Data!).RootElement
                .GetProperty("outcome").GetString());
            Assert.True(iterationStarts[index].Sequence < sequenceSettlements[index].Sequence);
            Assert.True(sequenceSettlements[index].Sequence < iterationCompletions[index].Sequence);
            if (index < iterationStarts.Length - 1)
                Assert.True(iterationCompletions[index].Sequence < iterationStarts[index + 1].Sequence);
        }
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Started && evt.NodeId is null);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Single_item_coordinator_binds_the_item_and_completes_one_sequence_activation()
    {
        var revision = CreateSequenceRevision("single-item-coordinator-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"only\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("only", result.Output);
        Assert.Equal(2, agent.Invocations.Length);
        Assert.All(agent.Invocations, invocation =>
        {
            Assert.Equal("only", invocation.Parameters!["item"]);
            Assert.Equal("0", invocation.Parameters["index"]);
        });
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/0");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted &&
            evt.ScopeId == "array-loop/0");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Empty_array_has_one_root_lifecycle_and_does_not_dispatch_a_body()
    {
        var revision = CreateSequenceRevision("empty-sequence-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.False(result.HasOutput);
        Assert.Empty(agent.Invocations);
        Assert.DoesNotContain(events, evt => evt.Kind.StartsWith("sequence.", StringComparison.Ordinal));
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.LoopIterationCompleted);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Item_failure_prevents_later_items_and_owns_one_root_failure()
    {
        var revision = CreateSequenceRevision("failed-sequence-proof");
        var agent = new CapturingAgentExecutor(failOnInput: "beta");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.True(result.Failed);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.DoesNotContain(agent.Invocations, invocation => invocation.Input == "gamma");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/1" &&
            JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "failed");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Cancellation_during_an_item_prevents_later_items_and_owns_one_root_cancellation()
    {
        var revision = CreateSequenceRevision("cancelled-sequence-proof");
        using var cancellation = new CancellationTokenSource();
        var agent = new CapturingAgentExecutor(cancelOnInput: cancellation);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.DoesNotContain(agent.Invocations, invocation => invocation.Input == "gamma");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.NodeId is null &&
            evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/1" &&
            JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "cancelled");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Cancelled);
    }

    [Fact]
    public async Task Rejected_sequence_evidence_keeps_only_the_accepted_root_prefix_without_a_terminal()
    {
        var revision = CreateSequenceRevision("rejected-sequence-evidence-proof");
        var events = new List<ExecutionEvent>();
        var rejection = new IOException("Controlled sequence evidence rejection.");
        var rejected = false;

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: new CapturingAgentExecutor())
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "index", "item", WorkflowDataTypes.String,
                evt =>
                {
                    if (evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && evt.ScopeId == "array-loop/0")
                    {
                        rejected = true;
                        return ValueTask.FromException(rejection);
                    }
                    events.Add(evt);
                    return ValueTask.CompletedTask;
                }));

        Assert.NotNull(failure);
        Assert.True(rejected);
        Assert.DoesNotContain(events, evt => evt.NodeId is null &&
            evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/1");
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
    }

    private static WorkflowRevision CreateSequenceRevision(string workflowId)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("body", "Body"), new("follow-up", "Follow-up")])
                })
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("follow-up-agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "out", "agent", "in", "data"))
                .Add("input-follow-up-agent", Edge("input", "out", "follow-up-agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("item", new("Item", "") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", Edge("input", "then", "sequence", "execute"))
                .Add("sequence-agent", Edge("sequence", "then.body", "agent", "execute"))
                .Add("sequence-follow-up-agent", Edge("sequence", "then.follow-up", "follow-up-agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
                .Add("follow-up-output", Edge("follow-up-agent", "out", "output", "execute"))
        };
        return WorkflowRevision.Create(definition, workflowId);
    }

    private static void AssertTerminalAndContiguous(IReadOnlyList<ExecutionEvent> events, string terminalKind)
    {
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == terminalKind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
        Assert.Equal(terminalKind, events[^1].Kind);
    }

    private static ConnectionDefinition Edge(string sourceNode, string sourcePort, string targetNode,
        string targetPort, string kind = "execution") =>
        new(new(sourceNode, sourcePort), new(targetNode, targetPort), kind);

    private sealed class CapturingAgentExecutor : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<Invocation> invocations = new();
        public Invocation[] Invocations => [.. invocations];

        public sealed record Invocation(string NodeId, string Input,
            IReadOnlyDictionary<string, string>? Parameters);

        public CapturingAgentExecutor(string? failOnInput = null, CancellationTokenSource? cancelOnInput = null)
        {
            this.failOnInput = failOnInput;
            this.cancelOnInput = cancelOnInput;
        }

        private readonly string? failOnInput;
        private readonly CancellationTokenSource? cancelOnInput;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.echo", "Proof Echo", "proof", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            invocations.Enqueue(new(invocation.NodeId, input, invocation.Parameters));
            if (input == failOnInput)
                throw new InvalidOperationException("Deterministic proof failure.");
            if (input == "beta" && cancelOnInput is not null)
            {
                cancelOnInput.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(input);
        }
    }
}
