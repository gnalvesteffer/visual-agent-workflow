using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ParallelExecutionTests
{
    [Fact]
    public async Task ParallelBranchesRunConcurrentlyAndJoinAsRecordedNamedJsonResults()
    {
        var definition = BranchesWithAgents();
        var executor = new ConcurrentAgentExecutor();
        var events = new List<ExecutionEvent>();
        var result = await new PipelineRunner(agents: executor).ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "input",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("{\"branch_1\":\"input:branch_1\",\"branch_2\":\"input:branch_2\"}", result);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("input:branch_1", json.RootElement.GetProperty("branch_1").GetString());
        Assert.Equal("input:branch_2", json.RootElement.GetProperty("branch_2").GetString());
        Assert.Equal(2, executor.MaximumConcurrentCalls);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeStarted && definition.Nodes[evt.NodeId!].Type == NodeTypes.Agent));
        var joinId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == joinId);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelJoinProgress && evt.NodeId == joinId));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.Transfer && evt.NodeId == joinId));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == joinId && evt.Data == result);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task BranchFailureFailsRunWithoutProducingPartialJoinResult()
    {
        var definition = BranchesWithAgents();
        var executor = new ConcurrentAgentExecutor("branch_2");
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner(agents: executor)
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        var joinId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        Assert.Equal(2, executor.MaximumConcurrentCalls);
        Assert.DoesNotContain(events, evt => evt.NodeId == joinId && evt.Kind == ExecutionEventKinds.NodeCompleted);
        Assert.DoesNotContain(events, evt => evt.NodeId == "output" && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
    }

    [Fact]
    public async Task JoinedPayloadOverLimitFailsWithoutEmittingOversizedOutput()
    {
        var definition = BranchesWithAgents();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner(agents: new ConcurrentAgentExecutor(responseLength: 9_000))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        var joinId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        Assert.DoesNotContain(events, evt => evt.NodeId == joinId && evt.Kind == ExecutionEventKinds.NodeCompleted);
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
    }

    [Fact]
    public async Task SequentialParallelBlocksExecuteAndProduceASeparateRecordedJoinForEachBlock()
    {
        var definition = new WorkflowDefinition("1.0", "sequential-parallel-runtime", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("uppercase", new(NodeTypes.Uppercase, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-uppercase", new(new("input", "out"), new("uppercase", "in"), "data"))
                .Add("uppercase-output", new(new("uppercase", "out"), new("output", "in"), "data")));
        definition = GraphEditing.InsertParallelBlockAfter(definition, "input", "out");
        var firstSelector = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.JsonSelectString).Key;
        definition = GraphEditing.ReplaceNode(definition, firstSelector,
            definition.Nodes[firstSelector] with { JsonPropertySelection = new("/branch_1") });
        definition = GraphEditing.InsertParallelBlockAfter(definition, firstSelector, "out");
        var secondSelectorId = definition.Nodes
            .Where(entry => entry.Value.Type == NodeTypes.JsonSelectString && entry.Key != firstSelector)
            .Select(entry => entry.Key)
            .Single();
        definition = GraphEditing.ReplaceNode(definition, secondSelectorId,
            definition.Nodes[secondSelectorId] with { JsonPropertySelection = new("/branch_1") });
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "go",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("GO", result);
        var joins = definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelJoin).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && joins.Contains(evt.NodeId!) && evt.Data is not null));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    private static WorkflowDefinition BranchesWithAgents()
    {
        var definition = new WorkflowDefinition("1.0", "parallel-runtime", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-output", new(new("input", "out"), new("output", "in"), "data")));
        definition = GraphEditing.InsertParallelBlockAfter(definition, "input", "out");
        var branchAnchors = definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelBranch).Select(entry => entry.Key).ToArray();
        foreach (var branchAnchor in branchAnchors)
        {
            var branch = definition.Connections.Values.Single(edge => edge.Target.NodeId == branchAnchor).Source.PortId;
            definition = GraphEditing.InsertNodeAfter(definition, branchAnchor, "out",
                new(NodeTypes.Agent, "1.0.0", Agent: new("test.parallel", "1.0.0", branch, [])));
        }
        return definition;
    }

    private sealed class ConcurrentAgentExecutor(string? failBranch = null, int responseLength = 0) : IAgentNodeExecutor
    {
        private readonly TaskCompletionSource bothBranchesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumConcurrentCalls;

        public int MaximumConcurrentCalls => maximumConcurrentCalls;

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("test.parallel", "1.0.0", "Test", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);
            if (active == 2) bothBranchesStarted.TrySetResult();
            try
            {
                await bothBranchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                var branch = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
                if (branch == failBranch) throw new InvalidOperationException("Branch fixture failure.");
                if (responseLength > 0) return new string('x', responseLength);
                return $"{input}:{branch}";
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
