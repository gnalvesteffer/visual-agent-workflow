using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ConfigurableParallelExecutionTests
{
    [Theory]
    [InlineData("1.32")]
    [InlineData("2.0")]
    public async Task ConfigurableForkRunsThreeBranchesConcurrentlyAndRecordsNamedJoinPayload(string schemaVersion)
    {
        var definition = Definition() with { SchemaVersion = schemaVersion };
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

    [Theory]
    [InlineData(true, "true", "selected")]
    [InlineData(false, "false", "skipped")]
    public async Task BranchLocalDecisionRunsOneOutcomeReconvergesAndRecordsStableBranchEvidence(
        bool decision, string expectedPort, string expectedAgent)
    {
        var definition = BranchLocalDefinition(decision);
        var executor = new BranchLocalExecutor();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(agents: executor).ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "request",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        using var json = JsonDocument.Parse(result);
        Assert.EndsWith($":{expectedAgent}:after", json.RootElement.GetProperty("branch_1").GetString());
        Assert.EndsWith(":Branch 2", json.RootElement.GetProperty("branch_2").GetString());
        Assert.Contains(expectedAgent, executor.Calls);
        Assert.DoesNotContain(expectedPort == "true" ? "skipped" : "selected", executor.Calls);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "after");
        using var decisionEvidence = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "route").Data!);
        Assert.Equal(expectedPort, decisionEvidence.RootElement.GetProperty("port").GetString());
        Assert.Equal("branch_1", decisionEvidence.RootElement.GetProperty("branchId").GetString());
        Assert.True(decisionEvidence.RootElement.TryGetProperty("parallelActivationId", out _));
        using var transferEvidence = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.NodeId == expectedAgent).Data!);
        Assert.Equal("branch_1", transferEvidence.RootElement.GetProperty("branchId").GetString());
        Assert.True(transferEvidence.RootElement.TryGetProperty("parallelActivationId", out _));
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

    private static WorkflowDefinition BranchLocalDefinition(bool decision)
    {
        var definition = Definition(2);
        var nodes = definition.Nodes
            .Add("condition", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(decision) })
            .Add("route", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("selected", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "selected", [])))
            .Add("skipped", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "skipped", [])))
            .Add("after", new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", null, "after", [])))
            .Remove("agent-branch_1");
        var execution = definition.ExecutionConnections!
            .Remove("branch_1-agent")
            .Remove("agent-branch_1-join")
            .Add("anchor-route", Edge("anchor_1", "then", "route", "execute"))
            .Add("route-true", Edge("route", "true", "selected", "execute"))
            .Add("route-false", Edge("route", "false", "skipped", "execute"))
            .Add("selected-after", Edge("selected", "out", "after", "execute"))
            .Add("skipped-after", Edge("skipped", "out", "after", "execute"))
            .Add("after-join", Edge("after", "out", "join", "branch.branch_1"));
        var data = definition.Connections.Add("condition-route",
            new(new("condition", "out"), new("route", "condition"), "data"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.BranchLocalParallelGraph,
            Nodes = nodes,
            Connections = data,
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

    private sealed class BranchLocalExecutor : IAgentNodeExecutor
    {
        private readonly List<string> calls = [];
        public IReadOnlyList<string> Calls => calls;
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.parallel", "Test", "test", true)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
        {
            var instruction = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
            lock (calls) calls.Add(instruction);
            return Task.FromResult($"{input}:{instruction}");
        }
    }
}
