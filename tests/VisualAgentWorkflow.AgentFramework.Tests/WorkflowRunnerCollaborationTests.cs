using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class WorkflowRunnerCollaborationTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(8, false)]
    [InlineData(2, true)]
    public async Task Schema135Workflow_ReleasesTwoOrderedSnapshotsAndJoinsEveryBranch(int branchCount, bool unified)
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new PhasedCollaborationAgentExecutor();
        string result;
        try
        {
            result = await new WorkflowRunner(agents: executor)
                .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(CreatePhasedGraph(branchCount, 30) with
                    { SchemaVersion = unified ? WorkflowSchemaVersions.UnifiedWorkflowGraph : WorkflowSchemaVersions.PhasedCollaborativeParallelGraph }, "test"), "question",
                    evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(error.Message + " EVENTS: " + string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ConnectionId}:{evt.Data}")), error);
        }

        using var output = JsonDocument.Parse(result);
        Assert.Equal(branchCount, output.RootElement.EnumerateObject().Count());
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        Assert.Equal(2 * branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationArrival));
        Assert.Equal(2 * branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent));
        Assert.Equal(2 * branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessagesRead));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
        var phases = events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("phase").GetString()).ToArray();
        Assert.Equal(new string?[] { ParallelCollaborationPhases.Phase1, ParallelCollaborationPhases.Phase2 }, phases);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
    }

    [Fact]
    public async Task Schema135PhaseTwoFailure_RetainsPhaseOneSnapshotAndSuppressesJoin()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents:
                new PhasedCollaborationAgentExecutor(failPhase: ParallelCollaborationPhases.Phase2, failBranch: "branch_b"))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(CreatePhasedGraph(3, 30), "test"), "question",
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Contains("p2-branch_b", error.Message, StringComparison.Ordinal);
        var releases = events.Where(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased).ToArray();
        Assert.Single(releases);
        Assert.Equal(ParallelCollaborationPhases.Phase1, JsonDocument.Parse(releases[0].Data!).RootElement.GetProperty("phase").GetString());
        var abort = events.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal("rendezvous-2", abort.NodeId);
        Assert.Contains("phase2", abort.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Schema135CancellationAfterSecondRelease_RecordsAbortAndSuppressesJoin()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var execution = new WorkflowRunner(agents: new PhasedCollaborationAgentExecutor()).ExecuteAsync(
            Guid.NewGuid(), WorkflowRevision.Create(CreatePhasedGraph(3, 30), "test"), "question",
            evt =>
            {
                events.Enqueue(evt);
                if (evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased &&
                    JsonDocument.Parse(evt.Data!).RootElement.GetProperty("phase").GetString() == ParallelCollaborationPhases.Phase2)
                    cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        var abort = events.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal("rendezvous-2", abort.NodeId);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema135PhaseTwoTimeout_PreservesPriorReleaseAndSuppressesJoin(bool afterSecondRelease)
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new PhasedCollaborationAgentExecutor(blockPhase: ParallelCollaborationPhases.Phase2,
            blockAfterRelease: afterSecondRelease);

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents: executor,
                collaborationOptions: new(TimeSpan.FromMilliseconds(150)))
            .ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(CreatePhasedGraph(3, null), "test"), "question",
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased &&
            JsonDocument.Parse(evt.Data!).RootElement.GetProperty("phase").GetString() == ParallelCollaborationPhases.Phase1);
        Assert.Equal(afterSecondRelease ? 2 : 1, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased));
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Schema133Workflow_RunsRealRendezvousAndJoinsBranchResultsAtConcurrencyOne(int branchCount)
    {
        var graph = CreateGraph(branchCount, timeoutSeconds: 30);
        var executor = new CollaborationAgentExecutor(branchCount);
        var events = new ConcurrentQueue<ExecutionEvent>();
        var revision = WorkflowRevision.Create(graph, "test");
        var runId = Guid.NewGuid();

        var result = await new WorkflowRunner(agents: executor)
            .ExecuteAsync(runId, revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(10));

        using var output = JsonDocument.Parse(result);
        Assert.Equal(branchCount, output.RootElement.EnumerateObject().Count());
        Assert.Equal(branchCount, executor.ReadBranches.Count);
        Assert.Equal(branchCount, executor.ReadBranches.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(branchCount, executor.ReadSnapshots.Count);
        Assert.All(executor.ReadSnapshots, snapshot => Assert.Equal(executor.ReadSnapshots[0], snapshot));
        Assert.Equal(1, executor.MaximumActiveCalls);
        Assert.Equal(branchCount * 2, executor.Calls);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationArrival));
        Assert.Equal(branchCount, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessagesRead));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(sequence => (long)sequence), events.Select(evt => evt.Sequence));
        Assert.Equal(ExecutionEventKinds.Completed, events.Last().Kind);

        using var start = JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted).Data!);
        Assert.Equal(1, start.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(30, start.RootElement.GetProperty("timeoutSeconds").GetDouble());
        Assert.True(start.RootElement.GetProperty("deadline").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        using var release = JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased).Data!);
        Assert.Equal(1, release.RootElement.GetProperty("version").GetInt32());
        var canonicalSnapshot = release.RootElement.GetProperty("canonicalJson").GetString()!;
        Assert.Equal(release.RootElement.GetProperty("utf8Bytes").GetInt32(), System.Text.Encoding.UTF8.GetByteCount(canonicalSnapshot));
        Assert.Equal(64, release.RootElement.GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task MissingWorkflowAndHostTimeouts_RejectsBeforeRunStarts()
    {
        var revision = WorkflowRevision.Create(CreateGraph(2, timeoutSeconds: null), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(2))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Contains("finite host default", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Fact]
    public async Task HostDefaultTimeout_IsRecordedInTheActivationEvent()
    {
        var revision = WorkflowRevision.Create(CreateGraph(2, timeoutSeconds: null), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new CollaborationAgentExecutor(2);
        var timeout = TimeSpan.FromSeconds(10);

        await new WorkflowRunner(agents: executor, collaborationOptions: new(timeout))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; });

        using var start = JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationRegionStarted).Data!);
        Assert.Equal(timeout.TotalSeconds, start.RootElement.GetProperty("timeoutSeconds").GetDouble());
    }

    [Fact]
    public async Task UnauthorizedChannel_IsDeniedAndNotRecorded()
    {
        var revision = WorkflowRevision.Create(CreateGraph(2, timeoutSeconds: 30), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new CollaborationAgentExecutor(2, attemptUnauthorizedChannel: true);

        await new WorkflowRunner(agents: executor)
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; });

        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationMessageSent));
        Assert.All(executor.UnauthorizedChannelRejections, rejected => Assert.True(rejected));
    }

    [Fact]
    public async Task FailedPublishAgent_AbortsWithoutSnapshotOrPostRendezvousAgentOutput()
    {
        var revision = WorkflowRevision.Create(CreateGraph(3, timeoutSeconds: 30), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3, failPublishBranch: "branch_b"))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.NodeId?.StartsWith("read-", StringComparison.Ordinal) == true && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task FailedReadAgent_RecordsReleaseThenAbortAndNeverCompletesJoin()
    {
        var revision = WorkflowRevision.Create(CreateGraph(3, timeoutSeconds: 30), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: new CollaborationAgentExecutor(3, failReadBranch: "branch_b"))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Fact]
    public async Task HostTimeoutBeforeRelease_AbortsRegionAndCancelsBlockedAgent()
    {
        var revision = WorkflowRevision.Create(CreateGraph(3, timeoutSeconds: null), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new CollaborationAgentExecutor(3, blockPublishUntilCancellation: true);

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents: executor,
            collaborationOptions: new(TimeSpan.FromMilliseconds(100)))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.DoesNotContain(events, evt => evt.NodeId?.StartsWith("read-", StringComparison.Ordinal) == true && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
    }

    [Fact]
    public async Task HostTimeoutAfterRelease_AbortsWithoutPartialJoinOutput()
    {
        var revision = WorkflowRevision.Create(CreateGraph(3, timeoutSeconds: null), "test");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var executor = new CollaborationAgentExecutor(3, blockReadUntilCancellation: true);

        await Assert.ThrowsAsync<TimeoutException>(() => new WorkflowRunner(agents: executor,
            collaborationOptions: new(TimeSpan.FromMilliseconds(150)))
            .ExecuteAsync(Guid.NewGuid(), revision, "question", evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Fact]
    public async Task CancellationAfterRelease_RecordsAbortBeforeReturningPartialOutput()
    {
        var revision = WorkflowRevision.Create(CreateGraph(3, timeoutSeconds: 30), "test");
        using var cancellation = new CancellationTokenSource();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var execution = new WorkflowRunner(agents: new CollaborationAgentExecutor(3)).ExecuteAsync(
            Guid.NewGuid(), revision, "question", evt =>
            {
                events.Enqueue(evt);
                if (evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationReleased);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ParallelCollaborationAborted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "join" && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    private static WorkflowDefinition CreateGraph(int branchCount, int? timeoutSeconds)
    {
        var branchIds = Enumerable.Range(0, branchCount).Select(index => $"branch_{(char)('a' + index)}").ToArray();
        var descriptors = branchIds.Select((branchId, index) => new ParallelBranchDescriptor(branchId, $"Branch {index + 1}", $"anchor-{branchId}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(descriptors) })
            .Add("rendezvous", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branchIds.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var branchId in branchIds)
        {
            var anchorId = $"anchor-{branchId}";
            var publishId = $"publish-{branchId}";
            var readId = $"read-{branchId}";
            nodes = nodes
                .Add(anchorId, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", branchId) })
                .Add(publishId, AgentNode(branchId))
                .Add(readId, AgentNode(branchId));
            execution = execution
                .Add($"fork-{branchId}", Edge("fork", ParallelBranchLimits.PinId(branchId), anchorId, "execute"))
                .Add($"anchor-{branchId}-publish", Edge(anchorId, "then", publishId, "execute"))
                .Add($"publish-{branchId}-rendezvous", Edge(publishId, "out", "rendezvous", ParallelBranchLimits.PinId(branchId)))
                .Add($"rendezvous-{branchId}-read", Edge("rendezvous", ParallelBranchLimits.PinId(branchId), readId, "execute"))
                .Add($"read-{branchId}-join", Edge(readId, "out", "join", ParallelBranchLimits.PinId(branchId)));
            regionBranches.Add(new(branchId,
            [
                new(publishId, ParallelCollaborationPhases.Publish, ["work"]),
                new(readId, ParallelCollaborationPhases.Read, ["work"])
            ]));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.CollaborativeParallelGraph, "collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous",
                new("fork", "rendezvous", "join", ["work"], timeoutSeconds, regionBranches.ToImmutable()))
        };
    }

    private static WorkflowDefinition CreatePhasedGraph(int branchCount, int? timeoutSeconds)
    {
        var branchIds = Enumerable.Range(0, branchCount).Select(index => $"branch_{(char)('a' + index)}").ToArray();
        var descriptors = branchIds.Select((branchId, index) => new ParallelBranchDescriptor(branchId, $"Branch {index + 1}", $"anchor-{branchId}")).ToImmutableArray();
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(descriptors) })
            .Add("rendezvous-1", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("rendezvous-2", new(NodeTypes.ParallelRendezvous, "1.0.0") { ParallelRendezvous = new(branchIds.ToImmutableArray()) })
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branchIds.ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"));
        var regionBranches = ImmutableArray.CreateBuilder<ParallelCollaborationBranch>();
        foreach (var branchId in branchIds)
        {
            var anchor = $"anchor-{branchId}";
            var p1 = $"p1-{branchId}";
            var r1 = $"r1-{branchId}";
            var p2 = $"p2-{branchId}";
            var r2 = $"r2-{branchId}";
            nodes = nodes.Add(anchor, new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", branchId) })
                .Add(p1, AgentNode(branchId)).Add(r1, AgentNode(branchId)).Add(p2, AgentNode(branchId)).Add(r2, AgentNode(branchId));
            execution = execution.Add($"fork-{branchId}", Edge("fork", ParallelBranchLimits.PinId(branchId), anchor, "execute"))
                .Add($"anchor-{branchId}-p1", Edge(anchor, "then", p1, "execute"))
                .Add($"p1-{branchId}-r1", Edge(p1, "out", "rendezvous-1", ParallelBranchLimits.PinId(branchId)))
                .Add($"r1-{branchId}-r1agent", Edge("rendezvous-1", ParallelBranchLimits.PinId(branchId), r1, "execute"))
                .Add($"r1agent-{branchId}-p2", Edge(r1, "out", p2, "execute"))
                .Add($"p2-{branchId}-r2", Edge(p2, "out", "rendezvous-2", ParallelBranchLimits.PinId(branchId)))
                .Add($"r2-{branchId}-r2agent", Edge("rendezvous-2", ParallelBranchLimits.PinId(branchId), r2, "execute"))
                .Add($"r2agent-{branchId}-join", Edge(r2, "out", "join", ParallelBranchLimits.PinId(branchId)));
            regionBranches.Add(new(branchId,
            [
                new(p1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(r1, ParallelCollaborationPhases.Phase1, ["work"]) { Role = ParallelCollaborationRoles.Read },
                new(p2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Publish },
                new(r2, ParallelCollaborationPhases.Phase2, ["work"]) { Role = ParallelCollaborationRoles.Read }
            ]));
        }
        execution = execution.Add("join-output", Edge("join", "then", "output", "execute"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-json", new(new("join", "out"), new("output", "in"), "data"));
        return new WorkflowDefinition(WorkflowSchemaVersions.PhasedCollaborativeParallelGraph, "phased-collaboration", "input", nodes, data)
        {
            ExecutionConnections = execution,
            ParallelCollaborationRegions = ImmutableSortedDictionary<string, ParallelCollaborationRegion>.Empty.Add("rendezvous-1",
                new("fork", "rendezvous-1", "join", ["work"], timeoutSeconds, regionBranches.ToImmutable()) { SecondRendezvousNodeId = "rendezvous-2" })
        };
    }

    private static NodeDefinition AgentNode(string branchId) =>
        new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, branchId, []));

    private static ConnectionDefinition Edge(string source, string sourcePort, string target, string targetPort) =>
        new(new(source, sourcePort), new(target, targetPort), "execution");

    private sealed class CollaborationAgentExecutor(int branchCount, string? failPublishBranch = null,
        string? failReadBranch = null, bool blockPublishUntilCancellation = false,
        bool blockReadUntilCancellation = false, bool attemptUnauthorizedChannel = false) : IAgentNodeExecutor
    {
        private readonly SemaphoreSlim permit = new(1, 1);
        private int activeCalls;
        private int maximumActiveCalls;
        private int calls;
        private readonly ConcurrentQueue<string> readBranches = new();
        private readonly ConcurrentQueue<string> readSnapshots = new();
        private readonly ConcurrentQueue<bool> unauthorizedChannelRejections = new();

        public int MaximumActiveCalls => Volatile.Read(ref maximumActiveCalls);
        public int Calls => Volatile.Read(ref calls);
        public IReadOnlyList<string> ReadBranches => readBranches.ToArray();
        public IReadOnlyList<string> ReadSnapshots => readSnapshots.ToArray();
        public IReadOnlyList<bool> UnauthorizedChannelRejections => unauthorizedChannelRejections.ToArray();
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
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
                    Assert.Equal(["work"], invocation.CollaborationSendChannels);
                    Assert.Null(invocation.CollaborationReadChannels);
                    Assert.NotNull(invocation.CollaborationSend);
                    Assert.Null(invocation.CollaborationRead);
                    await invocation.CollaborationSend!("work", $"Finding from {branchId}", cancellationToken);
                    if (attemptUnauthorizedChannel)
                    {
                        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                            invocation.CollaborationSend("unauthorized", "must not be appended", cancellationToken).AsTask());
                        Assert.Contains("grant", rejected.Message, StringComparison.OrdinalIgnoreCase);
                        unauthorizedChannelRejections.Enqueue(true);
                    }
                    if (branchId == failPublishBranch) throw new InvalidOperationException("fixture pre-rendezvous failure");
                    if (blockPublishUntilCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return input;
                }

                Assert.Null(invocation.CollaborationSendChannels);
                Assert.Equal(["work"], invocation.CollaborationReadChannels);
                Assert.Null(invocation.CollaborationSend);
                Assert.NotNull(invocation.CollaborationRead);
                var json = await invocation.CollaborationRead!("work", cancellationToken);
                using var snapshot = JsonDocument.Parse(json);
                Assert.Equal(branchCount, snapshot.RootElement.GetArrayLength());
                readBranches.Enqueue(branchId);
                readSnapshots.Enqueue(json);
                if (branchId == failReadBranch) throw new InvalidOperationException("fixture post-rendezvous failure");
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

    private sealed class PhasedCollaborationAgentExecutor(string? failPhase = null, string? failBranch = null,
        string? blockPhase = null, bool blockAfterRelease = false) : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<string> operations = new();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            var branch = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent!.Instructions;
            var phase = invocation.CollaborationPhase!;
            if (invocation.CollaborationRole == ParallelCollaborationRoles.Publish)
            {
                Assert.NotNull(invocation.CollaborationSend);
                Assert.Null(invocation.CollaborationRead);
                await invocation.CollaborationSend!("work", $"{phase}:{branch}", cancellationToken);
                operations.Enqueue($"publish:{phase}:{branch}");
                if (phase == failPhase && branch == failBranch) throw new InvalidOperationException("phase-two fixture failure");
                if (phase == blockPhase && !blockAfterRelease) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                Assert.Null(invocation.CollaborationSend);
                Assert.NotNull(invocation.CollaborationRead);
                var messages = await invocation.CollaborationRead!("work", cancellationToken);
                using var json = JsonDocument.Parse(messages);
                Assert.All(json.RootElement.EnumerateArray(), item =>
                    Assert.StartsWith($"{phase}:", item.GetProperty("content").GetString(), StringComparison.Ordinal));
                Assert.Contains(json.RootElement.EnumerateArray(), item =>
                    item.GetProperty("content").GetString() == $"{(phase == ParallelCollaborationPhases.Phase1 ? ParallelCollaborationPhases.Phase1 : ParallelCollaborationPhases.Phase2)}:{branch}");
                operations.Enqueue($"read:{phase}:{branch}");
                if (phase == blockPhase && blockAfterRelease) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return branch;
        }
    }
}
