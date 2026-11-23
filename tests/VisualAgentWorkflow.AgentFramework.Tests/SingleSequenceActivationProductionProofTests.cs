using System.Collections.Immutable;
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
        Assert.Equal(new[] { "one-item", "one-item" }, agent.Inputs);
        Assert.All(agent.Parameters, parameters => Assert.Equal("0", parameters!["index"]));

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

    private static ConnectionDefinition Edge(string sourceNode, string sourcePort, string targetNode,
        string targetPort, string kind = "execution") =>
        new(new(sourceNode, sourcePort), new(targetNode, targetPort), kind);

    private sealed class CapturingAgentExecutor : IAgentNodeExecutor
    {
        private readonly List<string> inputs = [];
        private readonly List<IReadOnlyDictionary<string, string>?> parameters = [];
        public string[] Inputs => [.. inputs];
        public IReadOnlyList<IReadOnlyDictionary<string, string>?> Parameters => parameters;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.echo", "Proof Echo", "proof", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inputs.Add(input);
            parameters.Add(invocation.Parameters);
            return Task.FromResult(input);
        }
    }
}
