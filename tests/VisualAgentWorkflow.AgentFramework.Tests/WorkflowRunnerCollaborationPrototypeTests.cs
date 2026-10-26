using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class WorkflowRunnerCollaborationPrototypeTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Schema132TestPlan_RunsBranchTargetedReadAfterReleaseAtConcurrencyOne(int branchCount)
    {
        var graph = CreateGraph(branchCount);
        var plan = CreatePlan(graph.Branches);
        var executor = new CollaborationAgentExecutor(branchCount);
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(graph.Definition, "test");
        var runId = Guid.NewGuid();
        var serializedDefinition = WorkflowJson.Serialize(revision.Definition);
        var result = await new WorkflowRunner(agents: executor) { CollaborationTestPlan = plan }
            .ExecuteAsync(runId, revision, "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        using var output = JsonDocument.Parse(result);
        Assert.Equal(branchCount, output.RootElement.EnumerateObject().Count());
        Assert.Equal(branchCount, executor.ReadBranches.Count);
        Assert.Equal(branchCount, executor.ReadBranches.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(branchCount, executor.ReadSnapshots.Count);
        Assert.All(executor.ReadSnapshots, snapshot => Assert.Equal(executor.ReadSnapshots[0], snapshot));
        Assert.Equal(1, executor.MaximumActiveCalls);
        Assert.Equal(serializedDefinition, WorkflowJson.Serialize(revision.Definition));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessagesRead));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationArrival));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);

        var header = new RunHeader(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, revision.Definition, "question");
        var captured = JsonSerializer.Serialize(new RunSnapshot(header, events.ToImmutableArray()));
        var playback = JsonSerializer.Deserialize<RunSnapshot>(captured)!;
        Assert.Equal(runId, playback.Header.RunId);
        Assert.Equal(events, playback.Events);
        Assert.Equal(branchCount, playback.Events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessagesRead));
        Assert.Equal(events.Select(evt => evt.Sequence), playback.Events.Select(evt => evt.Sequence));
        Assert.Equal(branchCount * 2, executor.Calls);
    }

    [Fact]
    public async Task FailedPrePhase_AbortsWithoutSnapshotOrPostAgentOutput()
    {
        var graph = CreateGraph(3);
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3, failPublishBranch: "branch_2"))
            { CollaborationTestPlan = CreatePlan(graph.Branches) }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.NodeId?.StartsWith("read-", StringComparison.Ordinal) == true && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        var playback = JsonSerializer.Deserialize<ExecutionEvent[]>(JsonSerializer.Serialize(events))!;
        Assert.Equal(events, playback);
        Assert.Contains(playback, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted && evt.Data is not null);
    }

    [Fact]
    public async Task FailedPostPhase_RecordsSnapshotThenAbortWithoutPartialWorkflowOutput()
    {
        var graph = CreateGraph(3);
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3, failReadBranch: "branch_2"))
            { CollaborationTestPlan = CreatePlan(graph.Branches) }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Fact]
    public async Task TimeoutBeforeRelease_AbortsWithoutDispatchingPostAgents()
    {
        var graph = CreateGraph(3);
        var events = new List<ExecutionEvent>();
        var plan = CreatePlan(graph.Branches) with
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            BeforeRelease = token => new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token))
        };

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3))
            { CollaborationTestPlan = plan }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.NodeId?.StartsWith("read-", StringComparison.Ordinal) == true && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
    }

    [Fact]
    public async Task TimeoutAfterSnapshotRelease_AbortsBeforePartialWorkflowOutput()
    {
        var graph = CreateGraph(3);
        var events = new List<ExecutionEvent>();
        var plan = CreatePlan(graph.Branches) with { Timeout = TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3, blockReadUntilCancellation: true))
            { CollaborationTestPlan = plan }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Fact]
    public async Task CancellationDuringReleaseHook_AbortsWithoutPostPhaseDispatch()
    {
        var graph = CreateGraph(3);
        using var cancellation = new CancellationTokenSource();
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<ExecutionEvent>();
        var plan = CreatePlan(graph.Branches) with
        {
            BeforeRelease = async token =>
            {
                releaseEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var execution = new WorkflowRunner(agents: new CollaborationAgentExecutor(3)) { CollaborationTestPlan = plan }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.NodeId?.StartsWith("read-", StringComparison.Ordinal) == true && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
    }

    [Fact]
    public async Task CancellationAfterSnapshotRelease_AbortsBeforePartialWorkflowOutput()
    {
        var graph = CreateGraph(3);
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();
        var execution = new WorkflowRunner(agents: new CollaborationAgentExecutor(3)) { CollaborationTestPlan = CreatePlan(graph.Branches) }
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(graph.Definition, "test"), "question", evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    private static ParallelCollaborationTestPlan CreatePlan(ImmutableArray<ParallelCollaborationTestBranch> branches) =>
        new("fork", "join", ["findings"], branches);

    private static (WorkflowDefinition Definition, ImmutableArray<ParallelCollaborationTestBranch> Branches) CreateGraph(int count)
    {
        var branches = Enumerable.Range(1, count).Select(index =>
            new ParallelCollaborationTestBranch($"branch_{index}", $"publish-{index}", $"read-{index}")).ToImmutableArray();
        var forkBranches = branches.Select((branch, index) => new ParallelBranchDescriptor(branch.BranchId, $"Branch {index + 1}", $"anchor-{index + 1}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(forkBranches) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branches.Select(branch => branch.BranchId).ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-data", new(new("join", "out"), new("output", "in"), "data"));

        foreach (var (branch, index) in branches.Select((branch, index) => (branch, index)))
        {
            var anchorId = $"anchor-{index + 1}";
            nodes = nodes.Add(anchorId, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", branch.BranchId) })
                .Add(branch.PublishAgentNodeId, new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, branch.BranchId, [])))
                .Add(branch.ReadAgentNodeId, new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, branch.BranchId, [])));
            execution = execution
                .Add($"fork-{index + 1}", Edge("fork", ParallelBranchLimits.PinId(branch.BranchId), anchorId, "execute"))
                .Add($"anchor-publish-{index + 1}", Edge(anchorId, "then", branch.PublishAgentNodeId, "execute"))
                .Add($"publish-read-{index + 1}", Edge(branch.PublishAgentNodeId, "out", branch.ReadAgentNodeId, "execute"))
                .Add($"read-join-{index + 1}", Edge(branch.ReadAgentNodeId, "out", "join", ParallelBranchLimits.PinId(branch.BranchId)));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.ConfigurableParallelGraph, "collaboration-prototype", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
        return (definition, branches);
    }

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    private sealed class CollaborationAgentExecutor(int branchCount, string? failPublishBranch = null, string? failReadBranch = null,
        bool blockReadUntilCancellation = false) : IAgentNodeExecutor
    {
        private readonly SemaphoreSlim permit = new(1, 1);
        private int activeCalls;
        private int maximumActiveCalls;
        private int calls;
        private readonly List<string> readBranches = [];
        private readonly List<string> readSnapshots = [];
        public int MaximumActiveCalls => maximumActiveCalls;
        public int Calls => calls;
        public IReadOnlyList<string> ReadBranches { get { lock (readBranches) return readBranches.ToArray(); } }
        public IReadOnlyList<string> ReadSnapshots { get { lock (readSnapshots) return readSnapshots.ToArray(); } }
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            await permit.WaitAsync(cancellationToken);
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);
            try
            {
                var branchId = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
                if (invocation.NodeId.StartsWith("publish-", StringComparison.Ordinal))
                {
                    Assert.NotNull(invocation.CollaborationSend);
                    Assert.Null(invocation.CollaborationRead);
                    await invocation.CollaborationSend!("findings", $"Finding from {branchId}", cancellationToken);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => invocation.CollaborationSend("not-granted", "bad", cancellationToken).AsTask());
                    if (branchId == failPublishBranch) throw new InvalidOperationException("fixture pre-phase failure");
                    return input;
                }

                Assert.Null(invocation.CollaborationSend);
                Assert.NotNull(invocation.CollaborationRead);
                var json = await invocation.CollaborationRead!("findings", cancellationToken);
                using var snapshot = JsonDocument.Parse(json);
                Assert.Equal(branchCount, snapshot.RootElement.GetArrayLength());
                lock (readBranches) readBranches.Add(branchId);
                lock (readSnapshots) readSnapshots.Add(json);
                if (branchId == failReadBranch) throw new InvalidOperationException("fixture post-phase failure");
                if (blockReadUntilCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return $"{branchId} read {branchCount} messages";
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
                permit.Release();
            }
        }

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref maximumActiveCalls);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximumActiveCalls, active, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }
}
