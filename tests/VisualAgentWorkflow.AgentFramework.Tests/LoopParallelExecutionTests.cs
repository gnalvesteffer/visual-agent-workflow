using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class LoopParallelExecutionTests
{
    [Fact]
    public async Task Public_runner_repeats_a_joined_loop_body_to_its_iteration_cap()
    {
        var definition = LoopWithParallelBody();
        var executor = new ParallelAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();

        string result;
        try
        {
            result = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
                WorkflowRevision.Create(definition, "loop-parallel"), "continue",
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(string.Join("\n", events.Select(evt =>
                $"{evt.Sequence} {evt.Kind} {evt.NodeId} {evt.ScopeId} {evt.Data}")), exception);
        }

        using var output = JsonDocument.Parse(result);
        Assert.Contains("continue:A", output.RootElement.GetProperty("a").GetString());
        Assert.EndsWith(":A", output.RootElement.GetProperty("a").GetString());
        Assert.Contains("continue:B", output.RootElement.GetProperty("b").GetString());
        Assert.EndsWith(":B", output.RootElement.GetProperty("b").GetString());
        Assert.Equal(4, executor.CallsByNode.Values.Sum());
        Assert.Equal(2, executor.CallsByNode["agent-a"]);
        Assert.Equal(2, executor.CallsByNode["agent-b"]);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var iterationStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        Assert.Equal(["loop/1", "loop/2"], iterationStarts.Select(evt => evt.ScopeId).Order(StringComparer.Ordinal));
        var forkActivations = events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelJoinActivated).ToArray();
        Assert.Equal(2, forkActivations.Length);
        var activationIds = forkActivations.Select(evt =>
        {
            using var evidence = JsonDocument.Parse(evt.Data!);
            return evidence.RootElement.GetProperty("ForkActivationId").GetGuid();
        });
        Assert.Equal(2, activationIds.Distinct().Count());
        Assert.Equal(2, events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelJoinArrival)
            .Select(evt => evt.ScopeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress));
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_loop_iteration_when_a_parallel_branch_fails()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(
            agents: new ParallelAgentExecutor(failedNodeId: "agent-b"))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(LoopWithParallelBody(), "loop-parallel-failure"),
                "continue", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Equal(ExecutionEventKinds.Failed, events.Last().Kind);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_loop_iteration_when_a_parallel_branch_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var runner = new WorkflowRunner(agents: new ParallelAgentExecutor(
            cancelledNodeId: "agent-b", cancellationSource: cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(LoopWithParallelBody(), "loop-parallel-cancellation"), "continue",
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.Equal(ExecutionEventKinds.Cancelled, events.Last().Kind);
    }

    private static WorkflowDefinition LoopWithParallelBody()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("a", "A", "anchor-a"),
            new ParallelBranchDescriptor("b", "B", "anchor-b"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("loop", new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 2) })
            .Add("body", new(NodeTypes.LoopBody, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) })
            .Add("anchor-a", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "a") })
            .Add("anchor-b", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "b") })
            .Add("agent-a", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "A", [])))
            .Add("agent-b", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "B", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", Edge("input", "then", "loop", "execute"))
            .Add("loop-body", Edge("loop", "body", "body", "execute"))
            .Add("body-fork", Edge("body", "then", "fork", "execute"))
            .Add("fork-a", Edge("fork", ParallelBranchLimits.PinId("a"), "anchor-a", "execute"))
            .Add("fork-b", Edge("fork", ParallelBranchLimits.PinId("b"), "anchor-b", "execute"))
            .Add("anchor-a-agent", Edge("anchor-a", "then", "agent-a", "execute"))
            .Add("anchor-b-agent", Edge("anchor-b", "then", "agent-b", "execute"))
            .Add("agent-a-join", Edge("agent-a", "out", "join", ParallelBranchLimits.PinId("a")))
            .Add("agent-b-join", Edge("agent-b", "out", "join", ParallelBranchLimits.PinId("b")))
            .Add("join-loop", Edge("join", "then", "loop", "feedback"))
            .Add("loop-output", Edge("loop", "done", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output", new(new("join", "out"), new("output", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "loop-parallel", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Edge(string source, string sourcePin, string target, string targetPin) =>
        new(new(source, sourcePin), new(target, targetPin), "execution");

    private sealed class ParallelAgentExecutor(string? failedNodeId = null, string? cancelledNodeId = null,
        CancellationTokenSource? cancellationSource = null) : IAgentNodeExecutor
    {
        private readonly ConcurrentDictionary<string, int> callsByNode = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> CallsByNode => callsByNode;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.parallel", "Test", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            callsByNode.AddOrUpdate(invocation.NodeId, 1, (_, count) => count + 1);
            if (invocation.NodeId == failedNodeId)
                throw new InvalidOperationException("Branch fixture failure.");
            if (invocation.NodeId == cancelledNodeId)
            {
                cancellationSource!.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
            return Task.FromResult($"{input}:{invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions}");
        }
    }
}
