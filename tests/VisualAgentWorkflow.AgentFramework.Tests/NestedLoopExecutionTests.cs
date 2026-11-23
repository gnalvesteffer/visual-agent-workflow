using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class NestedLoopExecutionTests
{
    [Fact]
    public async Task Public_runner_keeps_nested_loop_passes_and_scopes_independent()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new EchoAgentExecutor();

        var output = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NestedLoopGraph([2, 2]), "nested-loops"),
            "continue", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", output);
        Assert.Equal(4, executor.Calls);
        var iterationStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        var iterationCompletions = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).ToArray();
        Assert.Equal(6, iterationStarts.Length);
        Assert.Equal(6, iterationCompletions.Length);
        Assert.Equal(2, iterationStarts.Count(evt => evt.NodeId == "loop-0"));
        Assert.Equal(4, iterationStarts.Count(evt => evt.NodeId == "loop-1"));
        Assert.Equal(
            ["loop-0/1", "loop-0/1/loop-1/1", "loop-0/1/loop-1/2", "loop-0/2", "loop-0/2/loop-1/1", "loop-0/2/loop-1/2"],
            iterationStarts.Select(evt => evt.ScopeId).Order(StringComparer.Ordinal));
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopStopped));
    }

    [Fact]
    public async Task Public_runner_stops_nested_loop_work_at_the_root_aggregate_pass_budget()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new EchoAgentExecutor();
        var definition = NestedLoopGraph([16, 16, 16]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: executor).ExecuteAsync(
            Guid.NewGuid(), WorkflowRevision.Create(definition, "nested-loop-budget"), "continue",
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Equal(WorkflowLoopLimits.MaximumTotalBodyPassesPerRun,
            events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        var failure = Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
        Assert.Contains($"aggregate Loop body-pass limit ({WorkflowLoopLimits.MaximumTotalBodyPassesPerRun})", failure.Data);
        Assert.InRange(executor.Calls, 0, WorkflowLoopLimits.MaximumTotalBodyPassesPerRun);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_parent_iterations_after_a_nested_node_failure()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new EchoAgentExecutor(new InvalidOperationException("controlled failure"));

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkflowRunner(agents: executor).ExecuteAsync(
            Guid.NewGuid(), WorkflowRevision.Create(NestedLoopGraph([2, 2]), "nested-loop-failure"), "continue",
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Public_runner_does_not_complete_parent_iterations_after_nested_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new EchoAgentExecutor(onExecute: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner(agents: executor).ExecuteAsync(
            Guid.NewGuid(), WorkflowRevision.Create(NestedLoopGraph([2, 2]), "nested-loop-cancellation"), "continue",
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
    }

    private static WorkflowDefinition NestedLoopGraph(ImmutableArray<int> iterationLimits)
    {
        if (iterationLimits.Length < 2) throw new ArgumentException("At least two nested loops are required.", nameof(iterationLimits));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.Add("input", new(NodeTypes.Input, "1.0.0"));
        for (var index = 0; index < iterationLimits.Length; index++)
        {
            var loopId = $"loop-{index}";
            nodes = nodes
                .Add(loopId, new(NodeTypes.Loop, "1.0.0")
                { Loop = new(new("contains", "continue"), iterationLimits[index]) })
                .Add($"body-{index}", new(NodeTypes.LoopBody, "1.0.0"));
        }
        nodes = nodes.Add("agent", new(NodeTypes.Agent, "1.0.0",
                Agent: new("test.nested-loop", null, "Echo", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0"));

        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", Edge("input", "then", "loop-0", "execute"));
        for (var index = 0; index < iterationLimits.Length; index++)
        {
            var loopId = $"loop-{index}";
            var bodyId = $"body-{index}";
            execution = execution.Add($"{loopId}-body", Edge(loopId, "body", bodyId, "execute"));
            execution = index + 1 < iterationLimits.Length
                ? execution.Add($"{loopId}-next", Edge(bodyId, "then", $"loop-{index + 1}", "execute"))
                : execution.Add("body-agent", Edge(bodyId, "then", "agent", "execute"))
                    .Add("agent-feedback", Edge("agent", "out", loopId, "feedback"));
        }
        for (var index = iterationLimits.Length - 1; index > 0; index--)
            execution = execution.Add($"loop-{index}-done", Edge($"loop-{index}", "done", $"loop-{index - 1}", "feedback"));
        execution = execution.Add("loop-0-done", Edge("loop-0", "done", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("workflow-output", new(new("input", "out"), new("output", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "nested-loop", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Edge(string source, string sourcePin, string target, string targetPin) =>
        new(new(source, sourcePin), new(target, targetPin), "execution");

    private sealed class EchoAgentExecutor(Exception? failure = null, Action? onExecute = null) : IAgentNodeExecutor
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("test.nested-loop", "Nested Loop Test", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            onExecute?.Invoke();
            if (failure is not null) return Task.FromException<string>(failure);
            return Task.FromResult(input);
        }
    }
}
