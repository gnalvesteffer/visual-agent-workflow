using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ConfigurableParallelExecutionTests
{
    [Fact]
    public async Task ConfigurableForkRunsThreeBranchesConcurrentlyAndRecordsNamedJoinPayload()
    {
        var definition = Definition();
        var executor = new ThreeBranchExecutor();
        var events = new List<ExecutionEvent>();

        string result;
        try
        {
            result = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
                WorkflowRevision.Create(definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(string.Join("\n", events.Select(evt => $"{evt.Sequence} {evt.Kind} {evt.NodeId} {evt.ConnectionId} {evt.Data}")), exception);
        }

        using var json = JsonDocument.Parse(result);
        Assert.Equal("question:Branch 1", json.RootElement.GetProperty("branch_1").GetString());
        Assert.Equal("question:Branch 2", json.RootElement.GetProperty("branch_2").GetString());
        Assert.Equal("question:Branch 3", json.RootElement.GetProperty("branch_3").GetString());
        Assert.Equal(3, executor.MaximumConcurrentCalls);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join" && evt.Data == result);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == "join"));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task ConfigurableForkExecutesTheEightBranchLimitWithoutDroppingResults()
    {
        var definition = Definition(8);
        var executor = new ThreeBranchExecutor(expectedBranches: 8);
        var result = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "bounded",
            _ => ValueTask.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));

        using var json = JsonDocument.Parse(result);
        Assert.Equal(8, json.RootElement.EnumerateObject().Count());
        Assert.Equal(8, executor.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task ConfigurableJoinRejectsPartialResultWhenOneBranchFails()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new ThreeBranchExecutor(failBranch: "Branch 2"))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(Definition(), "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "output");
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
    }

    private static WorkflowDefinition Definition(int branchCount = 3)
    {
        var branches = Enumerable.Range(1, branchCount)
            .Select(index => new ParallelBranchDescriptor($"branch_{index}", $"Branch {index}", $"anchor_{index}"))
            .ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new("fork", branches.Select(branch => branch.Id).ToImmutableArray())
            })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"));
        foreach (var branch in branches)
        {
            nodes = nodes.Add(branch.AnchorNodeId, new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new("fork", branch.Id)
            }).Add($"agent-{branch.Id}", new(NodeTypes.Agent, "1.0.0",
                Agent: new("test.parallel", null, branch.Label, [])));
            execution = execution
                .Add($"fork-{branch.Id}", Edge("fork", ParallelBranchLimits.PinId(branch.Id), branch.AnchorNodeId, "execute"))
                .Add($"{branch.Id}-agent", Edge(branch.AnchorNodeId, "then", $"agent-{branch.Id}", "execute"))
                .Add($"agent-{branch.Id}-join", Edge($"agent-{branch.Id}", "out", "join", ParallelBranchLimits.PinId(branch.Id)));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.ConfigurableParallelGraph, "configurable-parallel-runtime", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    private sealed class ThreeBranchExecutor(string? failBranch = null, int expectedBranches = 3) : IAgentNodeExecutor
    {
        private readonly TaskCompletionSource allBranchesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumConcurrentCalls;
        private int calls;
        public int MaximumConcurrentCalls => maximumConcurrentCalls;
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.parallel", "Test", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCalls);
            var count = Interlocked.Increment(ref calls);
            UpdateMaximum(active);
            if (count == expectedBranches) allBranchesStarted.TrySetResult();
            try
            {
                await allBranchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                var instruction = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
                if (instruction == failBranch) throw new InvalidOperationException("Branch fixture failure.");
                return $"{input}:{instruction}";
            }
            finally { Interlocked.Decrement(ref activeCalls); }
        }

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref maximumConcurrentCalls);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximumConcurrentCalls, active, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }
}
