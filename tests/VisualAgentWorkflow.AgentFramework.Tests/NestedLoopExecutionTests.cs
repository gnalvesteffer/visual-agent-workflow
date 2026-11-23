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
    public async Task Pinned_workflow_calls_share_the_root_loop_body_pass_budget()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var child = WorkflowRevision.Create(LoopingChildGraph("loop-budget-child"), "loop-budget-child");
        var parent = WorkflowRevision.Create(WorkflowCallChain("loop-budget-parent", child), "loop-budget-parent");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor()).ExecuteWithLoopBudgetsProofAsync(
                Guid.NewGuid(), parent, "continue", maximumLoopPasses: 1, maximumLoopEvidenceEvents: 100,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call-1");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "call-2");
        Assert.Contains(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed &&
            evt.Data?.Contains("aggregate Loop body-pass limit", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Pinned_workflow_calls_share_the_root_loop_evidence_budget()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var child = WorkflowRevision.Create(LoopingChildGraph("loop-evidence-child"), "loop-evidence-child");
        var parent = WorkflowRevision.Create(WorkflowCallChain("loop-evidence-parent", child), "loop-evidence-parent");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor()).ExecuteWithLoopBudgetsProofAsync(
                Guid.NewGuid(), parent, "continue", maximumLoopPasses: 100, maximumLoopEvidenceEvents: 2,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        var loopEvents = events.Where(evt => evt.ScopeId?.Contains("/loop/", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, loopEvents.Length);
        Assert.Contains(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed &&
            evt.Data?.Contains("Loop-scoped evidence event limit (2)", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Parallel_pinned_workflow_calls_cannot_exceed_the_root_loop_pass_budget()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var child = WorkflowRevision.Create(LoopingChildGraph("parallel-loop-budget-child"), "parallel-loop-budget-child");
        var parent = WorkflowRevision.Create(ParallelWorkflowCallGraph("parallel-loop-budget-parent", child),
            "parallel-loop-budget-parent");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor()).ExecuteWithLoopBudgetsProofAsync(
                Guid.NewGuid(), parent, "continue", maximumLoopPasses: 1, maximumLoopEvidenceEvents: 100,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.InRange(events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted), 0, 1);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "output" && evt.ScopeId is null);
        Assert.Contains(events, evt => evt.Data?.Contains("aggregate Loop body-pass limit", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Nested_pinned_workflow_calls_share_the_root_loop_budget_and_scope_prefix()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var leaf = WorkflowRevision.Create(LoopingChildGraph("recursive-loop-budget-leaf"),
            "recursive-loop-budget-leaf");
        var middle = WorkflowRevision.Create(WorkflowCallChain("recursive-loop-budget-middle", leaf),
            "recursive-loop-budget-middle");
        var root = WorkflowRevision.Create(SingleWorkflowCallGraph("recursive-loop-budget-root", middle),
            "recursive-loop-budget-root");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(agents: new EchoAgentExecutor()).ExecuteWithLoopBudgetsProofAsync(
                Guid.NewGuid(), root, "continue", maximumLoopPasses: 1, maximumLoopEvidenceEvents: 100,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "call/call-1/loop/1");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed &&
            evt.ScopeId == "call/call-2");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null &&
            evt.Data?.Contains("aggregate Loop body-pass limit", StringComparison.Ordinal) == true);
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

    private static WorkflowDefinition LoopingChildGraph(string workflowId)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("loop", new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 1) })
            .Add("body", new(NodeTypes.LoopBody, "1.0.0"))
            .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new("test.nested-loop", null, "Echo", [])))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", Edge("input", "then", "loop", "execute"))
            .Add("loop-body", Edge("loop", "body", "body", "execute"))
            .Add("body-agent", Edge("body", "then", "agent", "execute"))
            .Add("agent-feedback", Edge("agent", "out", "loop", "feedback"))
            .Add("loop-output", Edge("loop", "done", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-agent", new(new("input", "out"), new("agent", "in"), "data"))
            .Add("input-output", new(new("input", "out"), new("output", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition WorkflowCallChain(string workflowId, WorkflowRevision child)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty;
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty;
        for (var index = 1; index <= 2; index++)
        {
            var callId = $"call-{index}";
            nodes = nodes.Add(callId, new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
            });
            var source = index == 1 ? "input" : $"call-{index - 1}";
            data = data.Add($"{source}-{callId}-data", new(new(source, "out"), new(callId, "in"), "data"));
            execution = execution.Add($"{source}-{callId}-execution",
                Edge(source, index == 1 ? "then" : "out", callId, "execute"));
        }
        nodes = nodes.Add("output", new(NodeTypes.Output, "1.0.0"));
        data = data.Add("call-output-data", new(new("call-2", "out"), new("output", "in"), "data"));
        execution = execution.Add("call-output-execution", Edge("call-2", "out", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition ParallelWorkflowCallGraph(string workflowId, WorkflowRevision child)
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("left", "Left", "anchor-left"),
            new ParallelBranchDescriptor("right", "Right", "anchor-right"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor-left", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "left") })
            .Add("anchor-right", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "right") })
            .Add("call-left", new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1,
                    child.ContentHash, child.Definition)
            })
            .Add("call-right", new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1,
                    child.ContentHash, child.Definition)
            })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
                { ParallelJoin = new("fork", ["left", "right"]) })
            .Add("output", new(NodeTypes.Output, "1.0.0")
                { OutputDataType = WorkflowDataTypes.Json });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-call-left", new(new("input", "out"), new("call-left", "in"), "data"))
            .Add("input-call-right", new(new("input", "out"), new("call-right", "in"), "data"))
            .Add("join-output", new(new("join", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"))
            .Add("fork-left", Edge("fork", ParallelBranchLimits.PinId("left"), "anchor-left", "execute"))
            .Add("fork-right", Edge("fork", ParallelBranchLimits.PinId("right"), "anchor-right", "execute"))
            .Add("left-call", Edge("anchor-left", "then", "call-left", "execute"))
            .Add("right-call", Edge("anchor-right", "then", "call-right", "execute"))
            .Add("left-join", Edge("call-left", "out", "join", "branch.left"))
            .Add("right-join", Edge("call-right", "out", "join", "branch.right"))
            .Add("join-output", Edge("join", "then", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition SingleWorkflowCallGraph(string workflowId, WorkflowRevision child)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("call", new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1,
                    child.ContentHash, child.Definition)
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-call", new(new("input", "out"), new("call", "in"), "data"))
            .Add("call-output", new(new("call", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-call", Edge("input", "then", "call", "execute"))
            .Add("call-output", Edge("call", "out", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input", nodes, data)
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
