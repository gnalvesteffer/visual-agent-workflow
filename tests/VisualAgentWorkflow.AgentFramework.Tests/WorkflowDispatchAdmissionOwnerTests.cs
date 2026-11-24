using System.Collections.Concurrent;
using System.Collections.Immutable;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class WorkflowDispatchAdmissionOwnerTests
{
    [Fact]
    public async Task Ordinary_production_builder_admits_every_node_before_start_and_drains_fork_join_tickets()
    {
        var revision = WorkflowRevision.Create(ParallelGraph(), "dispatch-admission-proof");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var result = await new WorkflowRunner(agents: new EchoAgentExecutor())
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "work",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), evt =>
                {
                    events.Enqueue(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                        timeline.Enqueue(("started", nodeId));
                    return ValueTask.CompletedTask;
                });

        Assert.False(result.Failed);
        var admissionEvents = timeline.ToArray();
        var outstanding = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, nodeId) in admissionEvents)
        {
            outstanding.TryGetValue(nodeId, out var count);
            if (kind == "admitted")
            {
                outstanding[nodeId] = count + 1;
                continue;
            }

            Assert.Equal("started", kind);
            Assert.True(count > 0, $"Node '{nodeId}' started without an outstanding admission.");
            outstanding[nodeId] = count - 1;
        }
        Assert.Equal(new[] { "input", "fork", "anchor-left", "anchor-right", "agent-left", "agent-right", "join", "join", "output" },
            admissionEvents.Where(entry => entry.Kind == "admitted").Select(entry => entry.NodeId)
                .OrderBy(nodeId => Array.IndexOf(AdmissionOrder, nodeId)).ToArray());
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Rejected_production_admission_does_not_start_the_target_node()
    {
        var revision = WorkflowRevision.Create(LinearGraph(), "dispatch-admission-rejection-proof");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor())
                .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "work",
                    ticket =>
                    {
                        if (ticket.TargetNodeId == "agent")
                            throw new InvalidOperationException("Admission rejected for proof.");
                    }, evt =>
                    {
                        events.Enqueue(evt);
                        return ValueTask.CompletedTask;
                    }));

        Assert.Contains("Admission rejected for proof.", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "agent");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
    }

    private static readonly string[] AdmissionOrder =
        ["input", "fork", "anchor-left", "anchor-right", "agent-left", "agent-right", "join", "output"];

    private static WorkflowDefinition ParallelGraph()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("left", "Left", "anchor-left"),
            new ParallelBranchDescriptor("right", "Right", "anchor-right"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor-left", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "left") })
            .Add("anchor-right", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "right") })
            .Add("agent-left", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo", null, "Left", []) { MaxTurns = 1 }))
            .Add("agent-right", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo", null, "Right", []) { MaxTurns = 1 }))
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["left", "right"]) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"))
            .Add("fork-left", Edge("fork", ParallelBranchLimits.PinId("left"), "anchor-left", "execute"))
            .Add("fork-right", Edge("fork", ParallelBranchLimits.PinId("right"), "anchor-right", "execute"))
            .Add("left-agent", Edge("anchor-left", "then", "agent-left", "execute"))
            .Add("right-agent", Edge("anchor-right", "then", "agent-right", "execute"))
            .Add("left-join", Edge("agent-left", "out", "join", ParallelBranchLimits.PinId("left")))
            .Add("right-join", Edge("agent-right", "out", "join", ParallelBranchLimits.PinId("right")))
            .Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-left", Edge("input", "out", "agent-left", "in", "data"))
            .Add("input-right", Edge("input", "out", "agent-right", "in", "data"))
            .Add("join-output-data", Edge("join", "out", "output", "in", "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "dispatch-admission-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition LinearGraph()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0",
                Agent: new("proof.echo", null, "Echo", []) { MaxTurns = 1 }))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent-data", Edge("input", "out", "agent", "in", "data"))
            .Add("agent-output-data", Edge("agent", "out", "output", "in", "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", Edge("input", "then", "agent", "execute"))
            .Add("agent-output", Edge("agent", "out", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "dispatch-admission-rejection-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort,
        string kind = "execution") => new(new(source, sourcePort), new(target, targetPort), kind);

    private sealed class EchoAgentExecutor : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("proof.echo", "Proof Echo", "proof", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(input);
        }
    }
}
