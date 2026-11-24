using System.Collections.Concurrent;
using System.Collections.Immutable;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
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

    private static readonly string[] RootTerminalKinds =
    [
        ExecutionEventKinds.Completed, ExecutionEventKinds.Failed, ExecutionEventKinds.Cancelled,
        ExecutionEventKinds.Terminated, ExecutionEventKinds.OutcomeUnknown
    ];

    [Fact]
    public async Task Ordinary_production_agent_failure_waits_for_held_sibling_and_fabricates_no_completion()
    {
        var revision = WorkflowRevision.Create(ParallelGraph(), "dispatch-admission-failure-proof");
        var agent = new HeldSiblingAgent(failNode: "agent-left");
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();

        var execution = new WorkflowRunner(agents: agent).ExecuteWithProductionDispatchAdmissionProofAsync(
            Guid.NewGuid(), revision, "work", ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)),
            evt => Observe(evt, events, timeline));
        await agent.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.FailureRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var settledEarly = await Task.WhenAny(execution, Task.Delay(300)) == execution;
        agent.Release();
        var exception = await Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(settledEarly == false, "The run settled while the sibling executor was still held.");
        Assert.NotNull(exception);
        AssertEveryStartedExecutorWasAdmitted(timeline);
        var recorded = events.ToArray();
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(recorded, evt => evt.NodeId == "output" && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(recorded, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
        Assert.Single(recorded, evt => evt.NodeId is null && RootTerminalKinds.Contains(evt.Kind));
    }

    [Fact]
    public async Task Ordinary_production_host_cancellation_waits_for_held_sibling_and_records_only_cancelled()
    {
        var revision = WorkflowRevision.Create(ParallelGraph(), "dispatch-admission-cancellation-proof");
        var agent = new HeldSiblingAgent(cancelNode: "agent-left");
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        using var cancellation = new CancellationTokenSource();

        var execution = new WorkflowRunner(agents: agent).ExecuteWithProductionDispatchAdmissionProofAsync(
            Guid.NewGuid(), revision, "work", ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)),
            evt => Observe(evt, events, timeline), cancellation.Token);
        await agent.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.CancellableStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var settledEarly = await Task.WhenAny(execution, Task.Delay(300)) == execution;
        agent.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(settledEarly == false, "The run settled while the sibling executor was still held.");
        AssertEveryStartedExecutorWasAdmitted(timeline);
        var recorded = events.ToArray();
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.DoesNotContain(recorded, evt => evt.NodeId == "output" && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Single(recorded, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Cancelled);
        Assert.Single(recorded, evt => evt.NodeId is null && RootTerminalKinds.Contains(evt.Kind));
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    public async Task Ordinary_production_run_reopens_from_store_with_contiguous_history_and_one_root_terminal(
        bool sqlite, string expectedStatus)
    {
        var revision = WorkflowRevision.Create(ParallelGraph(), $"dispatch-admission-store-{expectedStatus}");
        using var runStore = new ReopenableRunStore(revision, "work", sqlite);
        var agent = expectedStatus switch
        {
            "failed" => new HeldSiblingAgent(failNode: "agent-left", holdSibling: false),
            "cancelled" => new HeldSiblingAgent(cancelNode: "agent-left", holdSibling: false),
            _ => new HeldSiblingAgent(holdSibling: false)
        };
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var recorded = new List<ExecutionEvent>();
        var recordLock = new object();
        using var cancellation = new CancellationTokenSource();

        ValueTask Record(ExecutionEvent evt)
        {
            lock (recordLock)
            {
                runStore.Store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
                recorded.Add(evt);
            }
            if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                timeline.Enqueue(("started", nodeId));
            return ValueTask.CompletedTask;
        }

        var execution = new WorkflowRunner(agents: agent).ExecuteWithProductionDispatchAdmissionProofAsync(
            runStore.RunId, revision, "work", ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)),
            Record, cancellation.Token);
        switch (expectedStatus)
        {
            case "completed":
                Assert.False((await execution.WaitAsync(TimeSpan.FromSeconds(5))).Failed);
                break;
            case "failed":
                await Assert.ThrowsAsync<InvalidOperationException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                break;
            default:
                await agent.CancellableStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                break;
        }

        AssertEveryStartedExecutorWasAdmitted(timeline);
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(expectedStatus, reopened.Status);
        Assert.Equal(recorded, reopened.Events);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        var rootTerminal = Assert.Single(reopened.Events, evt => evt.NodeId is null && RootTerminalKinds.Contains(evt.Kind));
        Assert.Equal($"run.{expectedStatus}", rootTerminal.Kind);
        Assert.Equal(reopened.Events[^1], rootTerminal);
        Assert.Equal(expectedStatus == "completed",
            reopened.Events.Any(evt => evt.NodeId == "output" && evt.Kind == ExecutionEventKinds.NodeCompleted));
    }

    [Theory]
    [InlineData(false, ExecutionEventKinds.ParallelJoinArrival)]
    [InlineData(true, ExecutionEventKinds.ParallelJoinArrival)]
    [InlineData(false, ExecutionEventKinds.NodeCompleted)]
    [InlineData(true, ExecutionEventKinds.NodeCompleted)]
    public async Task Ordinary_production_lost_acknowledgement_store_fault_reopens_committed_prefix_as_running(
        bool sqlite, string faultedKind)
    {
        var (store, runStore, reopened, _) = await RunWithStoreFaultAsync(sqlite, faultedKind, commitBeforeFault: true);
        using var owner = runStore;

        // The runner still attempts node.failed/run.failed, but the store rejects them because they reuse the
        // committed event's sequence number, so persisted history is exactly the committed prefix.
        Assert.Equal(store.Committed, reopened.Events);
        Assert.Contains(reopened.Events, evt => evt.Sequence == store.FaultedSequence && evt.Kind == faultedKind);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        Assert.Equal("running", reopened.Status);
        Assert.DoesNotContain(reopened.Events, evt => evt.NodeId is null && RootTerminalKinds.Contains(evt.Kind));
    }

    /// <summary>
    /// Characterizes observed behavior: unlike the Sequence path (which marks the recorder uncertain), the ordinary
    /// path answers a rejected append by recording node.failed in place of the rejected event, then run.failed.
    /// The store accepts both because the sequence number was never consumed, so the reopened run is honestly
    /// failed rather than running, with the rejected event absent.
    /// </summary>
    [Theory]
    [InlineData(false, ExecutionEventKinds.ParallelJoinArrival)]
    [InlineData(true, ExecutionEventKinds.ParallelJoinArrival)]
    [InlineData(false, ExecutionEventKinds.NodeCompleted)]
    [InlineData(true, ExecutionEventKinds.NodeCompleted)]
    public async Task Ordinary_production_rejected_append_store_fault_reopens_as_honest_failed_run(
        bool sqlite, string faultedKind)
    {
        var (store, runStore, reopened, _) = await RunWithStoreFaultAsync(sqlite, faultedKind, commitBeforeFault: false);
        using var owner = runStore;

        Assert.Equal(store.Committed, reopened.Events);
        Assert.DoesNotContain(reopened.Events, evt => evt.Kind == faultedKind && evt.Sequence == store.FaultedSequence);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        Assert.Equal("failed", reopened.Status);
        var rootTerminal = Assert.Single(reopened.Events, evt => evt.NodeId is null && RootTerminalKinds.Contains(evt.Kind));
        Assert.Equal(ExecutionEventKinds.Failed, rootTerminal.Kind);
        Assert.Equal(reopened.Events[^1], rootTerminal);
        Assert.Equal(ExecutionEventKinds.NodeFailed, reopened.Events[^2].Kind);
        Assert.DoesNotContain(reopened.Events, evt => evt.NodeId == "output");
    }

    private static async Task<(FaultOnceRunStore Store, ReopenableRunStore RunStore, RunSnapshot Reopened,
        List<ExecutionEvent> Attempted)> RunWithStoreFaultAsync(bool sqlite, string faultedKind, bool commitBeforeFault)
    {
        var revision = WorkflowRevision.Create(ParallelGraph(), "dispatch-admission-store-fault");
        var runStore = new ReopenableRunStore(revision, "work", sqlite);
        var store = new FaultOnceRunStore(runStore.Store, faultedKind, commitBeforeFault);
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var attempted = new List<ExecutionEvent>();
        var recordLock = new object();

        ValueTask Record(ExecutionEvent evt)
        {
            lock (recordLock)
            {
                attempted.Add(evt);
                store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
            }
            if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                timeline.Enqueue(("started", nodeId));
            return ValueTask.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkflowRunner(agents: new HeldSiblingAgent(holdSibling: false))
            .ExecuteWithProductionDispatchAdmissionProofAsync(runStore.RunId, revision, "work",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), Record)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        AssertEveryStartedExecutorWasAdmitted(timeline);
        Assert.True(store.Faulted);
        Assert.Single(attempted, evt => evt.Kind == faultedKind && evt.Sequence == store.FaultedSequence);
        return (store, runStore, runStore.ReopenStore().Get(runStore.RunId)!, attempted);
    }

    private static ValueTask Observe(ExecutionEvent evt, ConcurrentQueue<ExecutionEvent> events,
        ConcurrentQueue<(string Kind, string NodeId)> timeline)
    {
        events.Enqueue(evt);
        if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
            timeline.Enqueue(("started", nodeId));
        return ValueTask.CompletedTask;
    }

    private static void AssertEveryStartedExecutorWasAdmitted(ConcurrentQueue<(string Kind, string NodeId)> timeline)
    {
        var outstanding = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, nodeId) in timeline)
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
    }

    private sealed class FaultOnceRunStore(IWorkflowRunStore inner, string eventKind, bool commitBeforeFault)
        : IWorkflowRunStore
    {
        public bool Faulted { get; private set; }
        public long FaultedSequence { get; private set; }
        public List<ExecutionEvent> Committed { get; } = [];

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            var injectFault = !Faulted && events.Any(evt => evt.Kind == eventKind);
            if (injectFault && !commitBeforeFault)
            {
                Faulted = true;
                FaultedSequence = events.First(evt => evt.Kind == eventKind).Sequence;
                throw new IOException($"Injected rejected {eventKind} append before commit.");
            }
            var snapshot = inner.Append(runId, expectedSequence, events);
            Committed.AddRange(events);
            if (!injectFault) return snapshot;
            Faulted = true;
            FaultedSequence = events.First(evt => evt.Kind == eventKind).Sequence;
            throw new IOException($"Injected committed {eventKind} append with lost acknowledgment.");
        }
    }

    private sealed class ReopenableRunStore : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"vaw-ordinary-admission-{Guid.NewGuid():N}");
        private readonly bool sqlite;

        public ReopenableRunStore(WorkflowRevision revision, string input, bool sqlite)
        {
            this.sqlite = sqlite;
            Directory.CreateDirectory(directory);
            Store = ReopenStore();
            RunId = Store.Create(revision, input).Header.RunId;
        }

        public IWorkflowRunStore Store { get; }
        public Guid RunId { get; }

        public IWorkflowRunStore ReopenStore() => sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
            : new FileRunJournal(directory);

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// agent-right is the "held" sibling (ignores cancellation until released) when holdSibling is true.
    /// agent-left fails or blocks until cancellation once the sibling has started.
    /// </summary>
    private sealed class HeldSiblingAgent(string? failNode = null, string? cancelNode = null, bool holdSibling = true)
        : IAgentNodeExecutor
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellableStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FailureRaised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.TrySetResult(true);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("proof.echo", "Proof Echo", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == "agent-right" && holdSibling)
            {
                HeldStarted.TrySetResult(true);
                await release.Task;
                return input;
            }

            if (invocation.NodeId == "agent-left" && holdSibling)
                await HeldStarted.Task.WaitAsync(cancellationToken);
            if (invocation.NodeId == failNode)
            {
                FailureRaised.TrySetResult(true);
                throw new InvalidOperationException("proof agent failed");
            }
            if (invocation.NodeId == cancelNode)
            {
                CancellableStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return input;
        }
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
