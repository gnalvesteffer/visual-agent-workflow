using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0081, "Sequence output Wait facts (2026-11-25)". Characterization only: the validator still rejects a Wait in a
/// Sequence output (<c>wait.placement</c>), so the runner is driven through a test-only seam that ignores exactly that issue
/// and otherwise uses the production dispatcher, admission owner, coordinator, and the host <see cref="ControlledRun"/> gate.
/// Waits are driven by <see cref="ManualTimeProvider"/>; the tests poll the journal in real time only to observe recorded events.
/// </summary>
public sealed class SequenceOutputWaitFactsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-seqwait-" + Guid.NewGuid());
    private readonly ManualTimeProvider clock = new(Start);
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly List<IDisposable> disposables = [];

    public void Dispose()
    {
        foreach (var disposable in disposables) disposable.Dispose();
        slot.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ConnectionDefinition Exec(string from, string fromPin, string to, string toPin) => new(new(from, fromPin), new(to, toPin), "execution");

    private static NodeDefinition Tool(string binding) => new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest(binding, new string('a', 64))) { McpArgumentsJson = "{}" };

    /// <summary>input -> Sequence[first, second]. first: [wait] -> tool "demo.first" -> output. second: tool "demo.second" -> output.</summary>
    private static WorkflowDefinition Graph(bool waitInFirst = true, long milliseconds = 60_000, bool approvalBeforeFirstTool = false, bool waitAfterApproval = false, bool waitInSecond = false, bool secondTerminates = false)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) },
            ["first-tool"] = Tool("demo.first"),
            ["second-tool"] = Tool("demo.second"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = Exec("input", "then", "sequence", "execute"),
            ["first-output"] = Exec("first-tool", "then", "output", "execute"),
            ["sequence-second"] = Exec("sequence", "then.second", waitInSecond ? "wait-second" : "second-tool", "execute"),
            ["second-output"] = Exec("second-tool", "then", "output", "execute")
        };
        if (secondTerminates)
        {
            nodes.Remove("second-tool");
            nodes["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) };
            execution.Remove("second-output");
            execution["sequence-second"] = Exec("sequence", "then.second", "terminate", "execute");
        }
        if (waitInSecond)
        {
            nodes["wait-second"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) };
            execution["second-chain"] = Exec("wait-second", "then", "second-tool", "execute");
        }
        // Head-to-tail chain of the first output: [wait] [approval] [wait-after-approval] first-tool.
        var chain = new List<string>();
        if (waitInFirst) { nodes["wait"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) }; chain.Add("wait"); }
        if (approvalBeforeFirstTool) { nodes["approval"] = new(NodeTypes.Approval, "1.0.0") { Approval = new("Allow the first tool?") }; chain.Add("approval"); }
        if (waitAfterApproval) { nodes["wait-after"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) }; chain.Add("wait-after"); }
        chain.Add("first-tool");
        execution["sequence-first"] = Exec("sequence", "then.first", chain[0], "execute");
        for (var i = 0; i + 1 < chain.Count; i++) execution[$"chain-{i}"] = Exec(chain[i], "then", chain[i + 1], "execute");
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty;
        if (approvalBeforeFirstTool) data = data.Add("approval-in", new(new("input", "out"), new("approval", "in"), "data"))
            .Add("approval-tool", new(new("approval", "out"), new("first-tool", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "sequence-output-wait", "input",
            nodes.ToImmutableSortedDictionary(), data)
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    private sealed class GateBroker(Func<string, CancellationToken, Task<string>> invoke) : INodeToolBroker
    {
        public List<string> Started { get; } = [];
        public Func<string, CancellationToken, Task<string>> Handler { get; } = invoke;
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(GateBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) => Invoke(bindingId, cancellationToken);
            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) => Invoke(bindingId, cancellationToken);
            private Task<string> Invoke(string bindingId, CancellationToken cancellationToken)
            {
                lock (owner.Started) owner.Started.Add(bindingId);
                return owner.Handler(bindingId, cancellationToken);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SignalingStore(IWorkflowRunStore inner) : IWorkflowRunStore
    {
        private readonly object gate = new();
        private readonly List<(Func<RunSnapshot, bool> Condition, TaskCompletionSource<RunSnapshot> Signal)> waiters = [];
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            var snapshot = inner.Append(runId, expectedSequence, events);
            lock (gate)
                foreach (var waiter in waiters.Where(waiter => waiter.Condition(snapshot)).ToArray())
                {
                    waiters.Remove(waiter);
                    waiter.Signal.TrySetResult(snapshot);
                }
            return snapshot;
        }
        public Task<RunSnapshot> WhenAsync(Guid runId, Func<RunSnapshot, bool> condition)
        {
            var signal = new TaskCompletionSource<RunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (inner.Get(runId) is { } current && condition(current)) return Task.FromResult(current);
                waiters.Add((condition, signal));
            }
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private sealed class Host : IDisposable
    {
        public required SignalingStore Store { get; init; }
        public required RunHeader Header { get; init; }
        public required ControlledRun Control { get; init; }
        public required Task<string?> Completion { get; set; }
        public Guid RunId => Header.RunId;
        public void Dispose() => Control.Dispose();
    }

    private Host StartHost(WorkflowDefinition definition, INodeToolBroker? broker = null, TimeSpan? grace = null,
        WorkflowWaitOptions? waits = null, int waitLimit = 1)
    {
        var store = new SignalingStore(new FileRunJournal(directory));
        var revision = WorkflowRevision.CreateProofOnly(definition, "test");
        var header = store.Create(revision, "hello").Header;
        var control = new ControlledRun(store, header, slot, clock, waits ?? new(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 4)) { ConcurrentWaitLimit = waitLimit };
        var host = new Host { Store = store, Header = header, Control = control, Completion = Task.FromResult<string?>(null) };
        disposables.Add(host);
        var runner = new WorkflowRunner(broker) { Clock = clock, SequenceAbandonGrace = grace };
        host.Completion = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(control.Cancellation.Token);
            try
            {
                var result = await runner.ExecuteIgnoringWaitPlacementProofAsync(header.RunId, revision, "hello", control.RecordAsync, control, linked.Token);
                return result.Output;
            }
            catch (OperationCanceledException) { return "cancelled"; }
            catch (Exception error) { return "faulted:" + error.GetType().Name + ":" + error.Message + (error.InnerException is { } inner ? " <- " + inner.GetType().Name + ":" + inner.Message : ""); }
        });
        return host;
    }

    private static int Count(RunSnapshot run, string kind) => run.Events.Count(evt => evt.Kind == kind);
    private static JsonElement Payload(ExecutionEvent evt) => JsonDocument.Parse(evt.Data!).RootElement;
    private string Dump(RunSnapshot run) => string.Join(Environment.NewLine, run.Events.Select(evt =>
        $"{evt.Sequence,3} {evt.Kind,-28} node={evt.NodeId} scope={evt.ScopeId} data={(evt.Data is { Length: > 140 } d ? d[..140] : evt.Data)}"));

    private static void AssertSettledExactlyOnce(RunSnapshot run)
    {
        var requested = run.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        var settled = run.Events.Where(evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted)
            .Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        Assert.Equal(requested.Order(), settled.Order());
    }

    private static GateBroker Instant() => new((_, _) => Task.FromResult("tool-result"));

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The awaited condition was not reached.");
            await Task.Delay(10);
        }
    }

    private Task WaitRequested(Host host, int count = 1) => host.Store.WhenAsync(host.RunId, run => Count(run, ExecutionEventKinds.WaitRequested) >= count);
    private Task Terminal(Host host) => host.Store.WhenAsync(host.RunId, run => run.IsTerminal);
    private static int Index(RunSnapshot run, string kind, string? nodeId = null) => run.Events.ToList().FindIndex(evt => evt.Kind == kind && (nodeId is null || evt.NodeId == nodeId));
    private static ExecutionEvent[] OutputEvents(RunSnapshot run, string kind, string outputId) =>
        run.Events.Where(evt => evt.Kind == kind && evt.Data is { } data && Payload(evt).TryGetProperty("outputId", out var id) && id.GetString() == outputId).ToArray();

    // ---- Question 1: concurrency (production path, no Wait, validated) -------------------------------------------

    [Fact]
    public async Task Production_dispatches_every_output_at_once_and_a_held_first_output_does_not_delay_the_second_tool()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new GateBroker((id, token) => id == "demo.first" ? release.Task.WaitAsync(token) : Task.FromResult("tool-result"));
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(Graph(waitInFirst: false), "test");
        var run = new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null, evt =>
        {
            lock (events) events.Add(evt);
            return ValueTask.CompletedTask;
        });

        await UntilAsync(() => { lock (broker.Started) return broker.Started.Contains("demo.first") && broker.Started.Contains("demo.second"); });
        ExecutionEvent[] whileHeld;
        lock (events) whileHeld = [.. events];
        // Both outputs were dispatched before either settled, and the second tool started and completed while the first is still held.
        Assert.Equal(2, whileHeld.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        Assert.DoesNotContain(whileHeld, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
        Assert.False(run.IsCompleted);

        release.SetResult("tool-result");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(result.Failed);
        ExecutionEvent[] all;
        lock (events) all = [.. events];
        var firstToolDone = all.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "first-tool");
        var secondToolDone = all.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "second-tool");
        var secondSettled = all.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && Payload(evt).GetProperty("outputId").GetString() == "second");
        Assert.True(secondToolDone < firstToolDone, "The second output's first node ran to completion while the first output's node was held.");
        // Agent Framework runs the outputs in lockstep supersteps: a later node of the second output starts only after the held first node ends.
        Assert.True(firstToolDone < secondSettled, "The second output's collector step cannot start until the superstep holding the first output ends.");
        Assert.True(SequenceEvidence.TryReconstructPlayback(all, out var playback));
        Assert.Equal("completed", playback!.Status);
    }

    // ---- Placement is unchanged -----------------------------------------------------------------------------------

    [Fact]
    public void The_validator_still_rejects_a_wait_in_a_sequence_output_and_the_path_profile_rejects_it_independently()
    {
        var issues = WorkflowValidator.Validate(Graph());

        Assert.Contains(issues, issue => issue.Code == "wait.placement" && issue.Path == "nodes.wait");
        // A second, independent gate: the unified Sequence path allowlist has no Wait.
        Assert.Contains(issues, issue => issue.Code == "sequence.path.node" && issue.Path == "nodes.wait");
        Assert.Empty(WorkflowValidator.Validate(Graph(waitInFirst: false)));
        // The production entry point refuses it too; only the test-only seam ignores exactly these two families.
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Create(Graph(), "test"));
    }

    // ---- Question 2: scope, lineage and evidence ------------------------------------------------------------------

    [Fact]
    public async Task Wait_events_in_a_sequence_output_carry_no_scope_or_lineage_and_attribute_only_through_the_invocation_id()
    {
        using var host = StartHost(Graph(), Instant());
        await WaitRequested(host);
        var pending = host.Store.Get(host.RunId)!;

        var requested = pending.Events.Single(evt => evt.Kind == ExecutionEventKinds.WaitRequested);
        Assert.Null(requested.ScopeId);
        Assert.False(Payload(requested).TryGetProperty("sequenceLineage", out _));
        Assert.False(Payload(requested).TryGetProperty("loopIteration", out _));
        // The node's own transfer evidence does carry the lineage; the invocation id is the only join key.
        var activated = pending.Events.Single(evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.NodeId == "wait");
        Assert.Equal(activated.InvocationId, requested.InvocationId);
        var lineage = Payload(activated).GetProperty("sequenceLineage");
        Assert.Equal("first", lineage.GetProperty("OutputId").GetString());
        Assert.Equal(0, lineage.GetProperty("Ordinal").GetInt32());
        // While the wait is pending the playback prefix is valid, the first output is dispatched, and wait events do not disturb it.
        Assert.True(SequenceEvidence.TryReconstructPlayback(pending.Events, out var prefix));
        Assert.Equal("running", prefix!.Status);
        Assert.Equal("dispatched", prefix.Outputs.Single(output => output.OutputId == "first").State);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
        var finished = host.Store.Get(host.RunId)!;
        Assert.Equal("completed", finished.Status);
        // wait.* events sit strictly between the output's dispatch and its terminal, and the state machine still accepts the journal.
        var dispatched = Index(finished, ExecutionEventKinds.SequenceOutputDispatched);
        var firstCompleted = finished.Events.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && Payload(evt).GetProperty("outputId").GetString() == "first");
        Assert.InRange(Index(finished, ExecutionEventKinds.WaitRequested), dispatched + 1, firstCompleted - 1);
        Assert.InRange(Index(finished, ExecutionEventKinds.WaitElapsed), dispatched + 1, firstCompleted - 1);
        Assert.True(SequenceEvidence.TryReconstructPlayback(finished.Events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.All(playback.Outputs, output => Assert.Equal("completed", output.State));
        AssertSettledExactlyOnce(finished);
        Assert.Empty(WorkflowWorkEvidence.Read(finished).UnresolvedWork);
    }

    [Fact]
    public async Task A_wait_stalls_the_sibling_output_after_its_first_node_because_outputs_advance_in_lockstep_supersteps()
    {
        using var host = StartHost(Graph(), Instant());
        await WaitRequested(host);
        var pending = host.Store.Get(host.RunId)!;
        // The sibling's first node already ran and completed concurrently with the wait request...
        Assert.Contains(pending.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "second-tool");

        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
        var finished = host.Store.Get(host.RunId)!;
        // ...but its Output step and its settlement come only after the wait elapsed.
        var secondSettled = finished.Events.ToList().FindIndex(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && Payload(evt).GetProperty("outputId").GetString() == "second");
        Assert.True(Index(finished, ExecutionEventKinds.WaitElapsed) < secondSettled);
    }

    // ---- Concurrency of two waits (Question 1 consequence) --------------------------------------------------------

    [Fact]
    public async Task Two_waits_in_two_outputs_collide_at_the_default_limit_and_the_first_settles_as_interrupted_once()
    {
        using var host = StartHost(Graph(waitInSecond: true), Instant());
        await Terminal(host);
        var run = host.Store.Get(host.RunId)!;

        Assert.Equal("failed", run.Status);
        Assert.StartsWith("faulted:", await host.Completion);
        // The loser never recorded a request; the winner's wait is settled by the failure cascade, not by an elapsed timer.
        Assert.Equal(1, Count(run, ExecutionEventKinds.WaitRequested));
        Assert.Equal(1, Count(run, ExecutionEventKinds.WaitInterrupted));
        Assert.Equal(0, Count(run, ExecutionEventKinds.WaitCancelled));
        AssertSettledExactlyOnce(run);
    }

    [Fact]
    public async Task With_a_limit_of_two_both_output_waits_run_concurrently_each_with_its_own_id_and_no_scope()
    {
        using var host = StartHost(Graph(waitInSecond: true), Instant(), waitLimit: 2);
        await WaitRequested(host, 2);
        var pending = host.Store.Get(host.RunId)!;
        var requests = pending.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).ToArray();
        Assert.Equal(["wait", "wait-second"], requests.Select(evt => evt.NodeId).Order());
        Assert.Equal(2, requests.Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).Distinct().Count());
        Assert.All(requests, evt => Assert.Null(evt.ScopeId));
        Assert.Equal([60_000L, 120_000L], requests.Select(evt => Payload(evt).GetProperty("bound").GetProperty("cumulativeRequestedMilliseconds").GetInt64()).Order());

        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
        var finished = host.Store.Get(host.RunId)!;
        Assert.Equal("completed", finished.Status);
        Assert.Equal(2, Count(finished, ExecutionEventKinds.WaitElapsed));
        AssertSettledExactlyOnce(finished);
        Assert.True(SequenceEvidence.TryReconstructPlayback(finished.Events, out var playback));
        Assert.Equal("completed", playback!.Status);
    }

    // ---- Question 3: stop, pause, failure, drain, abandon ---------------------------------------------------------

    [Fact]
    public async Task Stop_during_an_output_wait_settles_the_wait_and_each_output_and_the_sequence_exactly_once()
    {
        using var host = StartHost(Graph(), Instant());
        await WaitRequested(host);

        host.Control.Cancel();
        await Terminal(host);
        await host.Completion;
        clock.Advance(TimeSpan.FromHours(1));
        var run = host.Store.Get(host.RunId)!;

        Assert.Equal("cancelled", run.Status);
        Assert.Equal(1, Count(run, ExecutionEventKinds.WaitCancelled));
        Assert.Equal(0, Count(run, ExecutionEventKinds.WaitInterrupted));
        Assert.Equal(0, Count(run, ExecutionEventKinds.WaitElapsed));
        AssertSettledExactlyOnce(run);
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputCancelled, "first"));
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputCancelled, "second"));
        Assert.Equal(1, Count(run, ExecutionEventKinds.SequenceCancelled));
        Assert.Equal(0, Count(run, ExecutionEventKinds.SequenceFailed));
        Assert.True(SequenceEvidence.TryReconstructPlayback(run.Events, out var playback));
        Assert.Equal("cancelled", playback!.Status);
        Assert.Empty(WorkflowWorkEvidence.Read(run).UnresolvedWork);
    }

    [Fact]
    public async Task Pause_is_acknowledged_at_the_wait_once_the_sibling_is_quiet_the_clock_keeps_running_and_resume_completes()
    {
        using var host = StartHost(Graph(), Instant());
        await WaitRequested(host);

        host.Control.RequestPause();
        await host.Store.WhenAsync(host.RunId, run => run.Events.Any(evt => evt.Kind == ExecutionEventKinds.Paused));
        clock.Advance(TimeSpan.FromMinutes(1));
        var held = await host.Store.WhenAsync(host.RunId, run => Count(run, ExecutionEventKinds.WaitElapsed) == 1);
        Assert.False(held.IsTerminal);
        Assert.DoesNotContain(held.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "first-tool");

        host.Control.Resume();
        await Terminal(host);
        var finished = host.Store.Get(host.RunId)!;
        Assert.Equal("completed", finished.Status);
        AssertSettledExactlyOnce(finished);
    }

    [Fact]
    public async Task Known_gap_pause_is_acknowledged_at_the_wait_although_a_sibling_output_tool_is_still_executing()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new GateBroker((id, token) => id == "demo.second" ? release.Task.WaitAsync(token) : Task.FromResult("tool-result"));
        using var host = StartHost(Graph(), broker);
        await WaitRequested(host);
        await UntilAsync(() => { lock (broker.Started) return broker.Started.Contains("demo.second"); });

        host.Control.RequestPause();
        await host.Store.WhenAsync(host.RunId, run => run.Events.Any(evt => evt.Kind == ExecutionEventKinds.Paused));

        // Characterization of a defect that exists only because of the Wait (see the passing no-Wait control above): the sibling tool is still held, yet run.paused was recorded.
        // The runner's boundary calls keep the host's in-flight count at one for the whole Sequence, so the wait's request
        // drops it to zero. Fixing it needs per-output in-flight accounting (WaitForBranchStartAsync is not called for outputs).
        Assert.True(broker.Started.Contains("demo.second") && !release.Task.IsCompleted);
        Assert.Contains(host.Store.Get(host.RunId)!.Events, evt => evt.Kind == ExecutionEventKinds.Paused);

        release.SetResult("tool-result");
        host.Control.Resume();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
        Assert.Equal("completed", host.Store.Get(host.RunId)!.Status);
    }

    /// <summary>
    /// Control for the pause gap: with no Wait the same two-output Sequence does not acknowledge a pause while a sibling's
    /// tool is executing, because Agent Framework supersteps run in lockstep and the next boundary is not reached until the
    /// held tool returns. The gap exists only because a Wait lowers the host's in-flight count.
    /// </summary>
    [Fact]
    public async Task Pause_without_any_wait_is_not_acknowledged_while_a_sibling_output_tool_is_executing()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new GateBroker((id, token) => id == "demo.second" ? release.Task.WaitAsync(token) : Task.FromResult("tool-result"));
        using var host = StartHost(Graph(waitInFirst: false), broker);
        await UntilAsync(() => { lock (broker.Started) return broker.Started.Contains("demo.second") && broker.Started.Contains("demo.first"); });
        await host.Store.WhenAsync(host.RunId, run => Index(run, ExecutionEventKinds.NodeCompleted, "first-tool") >= 0);

        host.Control.RequestPause();
        await Task.Delay(500);
        var held = host.Store.Get(host.RunId)!;
        Assert.False(release.Task.IsCompleted);
        Assert.DoesNotContain(held.Events, evt => evt.Kind == ExecutionEventKinds.Paused);

        release.SetResult("tool-result");
        await host.Store.WhenAsync(host.RunId, run => run.Events.Any(evt => evt.Kind == ExecutionEventKinds.Paused));
        host.Control.Resume();
        await Terminal(host);
        Assert.Equal("completed", host.Store.Get(host.RunId)!.Status);
    }

    [Fact(Skip = "DESIRED BEHAVIOR, not implemented: a pause must not be acknowledged while a sibling output's node is executing. " +
        "ControlledRun keeps one in-flight count for the whole Sequence, so a Wait in one output (still rejected by the validator) " +
        "drops it to zero. Fixing it needs per-node in-flight tracking across concurrent outputs; see Decision 0081, " +
        "'Sequence output pause acknowledgement design note'.")]
    public async Task A_pause_is_not_acknowledged_at_a_wait_while_a_sibling_output_tool_is_executing()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new GateBroker((id, token) => id == "demo.second" ? release.Task.WaitAsync(token) : Task.FromResult("tool-result"));
        using var host = StartHost(Graph(), broker);
        await WaitRequested(host);
        await UntilAsync(() => { lock (broker.Started) return broker.Started.Contains("demo.second"); });

        host.Control.RequestPause();
        await Task.Delay(500);
        Assert.DoesNotContain(host.Store.Get(host.RunId)!.Events, evt => evt.Kind == ExecutionEventKinds.Paused);

        release.SetResult("tool-result");
        host.Control.Resume();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
    }

    [Fact]
    public async Task A_failing_sibling_output_settles_the_pending_wait_as_interrupted_with_the_host_stopped_reason()
    {
        var failNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new GateBroker(async (id, token) =>
        {
            if (id != "demo.second") return "tool-result";
            await failNow.Task.WaitAsync(token);
            throw new InvalidOperationException("boom");
        });
        using var host = StartHost(Graph(), broker);
        await WaitRequested(host);
        failNow.SetResult();
        await Terminal(host);
        await host.Completion;
        var run = host.Store.Get(host.RunId)!;

        Assert.Equal("failed", run.Status);
        // Finding: the wait is not wait.cancelled (the host never asked to stop) and its reason text blames the host.
        var settlement = Assert.Single(run.Events, evt => evt.Kind is ExecutionEventKinds.WaitInterrupted or ExecutionEventKinds.WaitCancelled);
        Assert.Equal(ExecutionEventKinds.WaitInterrupted, settlement.Kind);
        Assert.Contains("host stopped", Payload(settlement).GetProperty("reason").GetString(), StringComparison.Ordinal);
        AssertSettledExactlyOnce(run);
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputFailed, "second"));
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputCancelled, "first"));
        Assert.Equal(1, Count(run, ExecutionEventKinds.SequenceFailed));
        Assert.True(SequenceEvidence.TryReconstructPlayback(run.Events, out var playback));
        Assert.Equal("failed", playback!.Status);
    }

    [Fact]
    public async Task A_failure_termination_in_a_sibling_output_is_invisible_while_the_wait_runs_so_drain_and_abandon_grace_cannot_act()
    {
        using var host = StartHost(Graph(secondTerminates: true), Instant(), grace: TimeSpan.FromSeconds(5));
        await WaitRequested(host);

        // The grace is shorter than the wait. It never starts because the failed output is only settled by the collector,
        // which runs in the superstep after the wait.
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        var during = host.Store.Get(host.RunId)!;
        Assert.False(during.IsTerminal);
        Assert.DoesNotContain(during.Events, evt => evt.Kind is ExecutionEventKinds.SequenceOutputFailed or ExecutionEventKinds.SequenceOutputAbandoned);
        Assert.Equal(1, Count(during, ExecutionEventKinds.WaitRequested));
        Assert.Equal(0, Count(during, ExecutionEventKinds.WaitElapsed));

        clock.Advance(TimeSpan.FromMinutes(1));
        await Terminal(host);
        var run = host.Store.Get(host.RunId)!;
        Assert.Equal("failed", run.Status);
        // Drain semantics: the waiting output ran to completion after the sibling's failure, then the Sequence ended failed.
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputFailed, "second"));
        Assert.Single(OutputEvents(run, ExecutionEventKinds.SequenceOutputCompleted, "first"));
        Assert.Equal(1, Count(run, ExecutionEventKinds.SequenceFailed));
        AssertSettledExactlyOnce(run);
    }

    // ---- Question 4: authorization-to-outcome hazard --------------------------------------------------------------

    [Fact]
    public async Task A_wait_between_an_approval_and_its_tool_is_rejected_by_the_approval_rule_regardless_of_placement()
    {
        var hazard = Graph(waitInFirst: false, approvalBeforeFirstTool: true, waitAfterApproval: true);

        Assert.Contains(WorkflowValidator.Validate(hazard), issue => issue.Code == "approval.target" && issue.Path == "nodes.approval");
        // The test-only seam ignores only wait.placement and sequence.path.*, so this hazard stays unreachable through it too.
        var revision = WorkflowRevision.CreateProofOnly(hazard, "test");
        var header = new FileRunJournal(directory).Create(revision, "x").Header;
        using var control = new ControlledRun(new FileRunJournal(directory), header, slot, clock, WorkflowWaitOptions.Default);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new WorkflowRunner(Instant())
            .ExecuteIgnoringWaitPlacementProofAsync(header.RunId, revision, "hello", _ => ValueTask.CompletedTask, control));
        Assert.Contains("approval.target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_before_an_approval_completes_before_the_request_and_nothing_separates_the_permit_from_the_tool()
    {
        var definition = Graph(approvalBeforeFirstTool: true);
        var onlyExpected = WorkflowValidator.Validate(definition).Select(issue => issue.Code).Distinct().Order().ToArray();
        Assert.All(onlyExpected, code => Assert.True(code == "wait.placement" || code.StartsWith("sequence.path.", StringComparison.Ordinal), code));

        using var host = StartHost(definition, Instant());
        await WaitRequested(host);
        Assert.DoesNotContain(host.Store.Get(host.RunId)!.Events, evt => evt.Kind == ExecutionEventKinds.ApprovalRequested);
        clock.Advance(TimeSpan.FromMinutes(1));
        var waiting = await host.Store.WhenAsync(host.RunId, run => WorkflowApprovalEvidence.Pending(run) is not null);
        var request = WorkflowApprovalEvidence.Pending(waiting)!;
        host.Control.DecideApproval(request.RequestId, request.ActionHash, true, "tester");
        await Terminal(host);
        var run = host.Store.Get(host.RunId)!;

        Assert.Equal("completed", run.Status);
        var elapsed = Index(run, ExecutionEventKinds.WaitElapsed);
        var requested = Index(run, ExecutionEventKinds.ApprovalRequested);
        var approved = Index(run, ExecutionEventKinds.ApprovalApproved);
        var toolStarted = Index(run, ExecutionEventKinds.NodeStarted, "first-tool");
        var toolDone = Index(run, ExecutionEventKinds.NodeCompleted, "first-tool");
        Assert.True(elapsed < requested && requested < approved && approved < toolStarted && toolStarted < toolDone);
        Assert.Equal(1, Count(run, ExecutionEventKinds.WaitRequested));
        Assert.DoesNotContain(run.Events.Skip(approved + 1).Take(toolDone - approved), evt => evt.Kind.StartsWith("wait.", StringComparison.Ordinal));
    }
}
