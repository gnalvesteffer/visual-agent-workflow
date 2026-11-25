using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Wait first slice (Decision 0081, 2026-11-24). Every duration is driven by <see cref="ManualTimeProvider"/>: nothing
/// sleeps for a wait, and races are decided by the order the test calls Advance, Cancel, and shutdown.
/// </summary>
public sealed class WaitExecutionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-wait-" + Guid.NewGuid());
    private readonly ManualTimeProvider clock = new(Start);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static NodeDefinition Wait(long milliseconds) => new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) };

    /// <summary>Input -> wait1 -> ... -> output, the input text reaching the output by a data edge.</summary>
    private static WorkflowDefinition WaitWorkflow(params long[] durations)
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        var previous = ("input", "then");
        for (var index = 0; index < durations.Length; index++)
        {
            var id = $"wait{index + 1}";
            nodes[id] = Wait(durations[index]);
            execution[$"to-{id}"] = new(new(previous.Item1, previous.Item2), new(id, "execute"), "execution");
            previous = (id, "then");
        }
        execution["finish"] = new(new(previous.Item1, previous.Item2), new("output", "execute"), "execution");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-workflow", "input", nodes.ToImmutable(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("value", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    private static readonly WorkflowDefinition PlainWorkflow = WorkflowTemplates.Create("uppercase");

    // ---- runner fail-closed ------------------------------------------------------------------------------------

    private sealed class PlainGate : IWorkflowExecutionGate
    {
        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_runner_without_a_wait_capable_gate_fails_closed_before_any_node_runs()
    {
        var revision = WorkflowRevision.Create(WaitWorkflow(1_000), "test");
        foreach (var run in new Func<List<ExecutionEvent>, Task>[]
        {
            events => new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "abc", evt => { events.Add(evt); return ValueTask.CompletedTask; }),
            events => new WorkflowRunner().ExecuteControlledAsync(Guid.NewGuid(), revision, "abc", evt => { events.Add(evt); return ValueTask.CompletedTask; }, new PlainGate())
        })
        {
            var events = new List<ExecutionEvent>();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run(events));

            Assert.Contains("Wait node", error.Message, StringComparison.Ordinal);
            Assert.Contains("IWorkflowWaitGate", error.Message, StringComparison.Ordinal);
            Assert.Empty(events);
        }
    }

    /// <summary>Wraps a journal so tests await a recorded condition instead of polling, and can make appends fail.</summary>
    private sealed class SignalingStore(IWorkflowRunStore inner) : IWorkflowRunStore
    {
        private readonly object gate = new();
        private readonly List<(Guid RunId, Func<RunSnapshot, bool> Condition, TaskCompletionSource<RunSnapshot> Signal)> waiters = [];
        public volatile bool FailAppends;

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            if (FailAppends) throw new IOException("The journal is unwritable.");
            var snapshot = inner.Append(runId, expectedSequence, events);
            lock (gate)
                foreach (var waiter in waiters.Where(waiter => waiter.RunId == runId && waiter.Condition(snapshot)).ToArray())
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
                waiters.Add((runId, condition, signal));
            }
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    // ---- ControlledRun gate ------------------------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        public required SignalingStore Store { get; init; }
        public required RunHeader Header { get; init; }
        public required SemaphoreSlim Slot { get; init; }
        public required ControlledRun Run { get; init; }
        public IReadOnlyList<ExecutionEvent> Events => Store.Get(Header.RunId)!.Events;
        public string[] Kinds => [.. Events.Select(evt => evt.Kind)];
        public string[] WaitKinds => [.. Events.Select(evt => evt.Kind).Where(kind => kind.StartsWith("wait.", StringComparison.Ordinal))];
        public void Dispose() { Run.Dispose(); Slot.Dispose(); }
    }

    private Harness Create(WorkflowDefinition definition, WorkflowWaitOptions? options = null)
    {
        var store = new SignalingStore(new FileRunJournal(directory));
        var header = store.Create(WorkflowRevision.Create(definition, "test"), "abc").Header;
        var slot = new SemaphoreSlim(1, 1);
        return new() { Store = store, Header = header, Slot = slot, Run = new(store, header, slot, clock, options) };
    }

    private static WorkflowWaitRequest Request(string node, long milliseconds) =>
        new(node, Guid.NewGuid(), TimeSpan.FromMilliseconds(milliseconds));

    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Awaits the recorder's signal for an event; never advances or sleeps on the fake clock.</summary>
    private static Task Recorded(Harness harness, string kind) =>
        harness.Store.WhenAsync(harness.Header.RunId, snapshot => snapshot.Events.Any(evt => evt.Kind == kind));

    /// <summary>Begins a wait: takes the slot as the runner does, then starts the wait on the thread pool.</summary>
    private async Task<(Task Wait, CancellationTokenSource? Source)> BeginWait(Harness harness, string node, long milliseconds,
        CancellationToken token = default)
    {
        await harness.Run.WaitForBoundaryAsync(node, token);
        Assert.Equal(0, harness.Slot.CurrentCount);
        var wait = harness.Run.WaitAsync(Request(node, milliseconds), token).AsTask();
        return (wait, null);
    }

    [Fact]
    public async Task A_wait_releases_the_execution_slot_records_its_request_and_reacquires_the_slot_after_elapsing()
    {
        using var harness = Create(WaitWorkflow(60_000), new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 4));
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, harness.Run.Cancellation.Token);

        Assert.Equal(1, harness.Slot.CurrentCount);
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, clock.ActiveTimerCount);
        var requested = Assert.Single(harness.Events, evt => evt.Kind == ExecutionEventKinds.WaitRequested);
        Assert.Equal("wait1", requested.NodeId);
        Assert.NotNull(requested.InvocationId);
        using (var data = JsonDocument.Parse(requested.Data!))
        {
            var root = data.RootElement;
            Assert.True(root.GetProperty("waitId").TryGetGuid(out var waitId) && waitId != Guid.Empty);
            Assert.Equal("wait1", root.GetProperty("nodeId").GetString());
            Assert.Equal(60_000, root.GetProperty("durationMilliseconds").GetInt64());
            Assert.Equal(Start.AddMinutes(1), root.GetProperty("deadlineUtc").GetDateTimeOffset());
            var bound = root.GetProperty("bound");
            Assert.Equal(300_000, bound.GetProperty("perWaitMaximumMilliseconds").GetInt64());
            Assert.Equal(600_000, bound.GetProperty("cumulativeBudgetMilliseconds").GetInt64());
            Assert.Equal(60_000, bound.GetProperty("cumulativeRequestedMilliseconds").GetInt64());
        }

        clock.Advance(TimeSpan.FromMilliseconds(59_999));
        Assert.False(wait.IsCompleted);
        Assert.Equal(["wait.requested"], harness.WaitKinds);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Within(wait);

        Assert.Equal(["wait.requested", "wait.elapsed"], harness.WaitKinds);
        Assert.Equal(0, harness.Slot.CurrentCount);
        var elapsed = harness.Events.Single(evt => evt.Kind == ExecutionEventKinds.WaitElapsed);
        Assert.Equal(requested.InvocationId, elapsed.InvocationId);
        Assert.Equal(Start.AddMinutes(1), elapsed.Timestamp);
        using var settled = JsonDocument.Parse(elapsed.Data!);
        Assert.Equal(60_000, settled.RootElement.GetProperty("elapsedMilliseconds").GetInt64());
        Assert.Equal(JsonDocument.Parse(requested.Data!).RootElement.GetProperty("waitId").GetGuid(), settled.RootElement.GetProperty("waitId").GetGuid());
    }

    [Fact]
    public async Task The_duration_is_measured_on_the_monotonic_clock_and_the_utc_deadline_is_display_only()
    {
        using var harness = Create(WaitWorkflow(10_000));
        var (wait, _) = await BeginWait(harness, "wait1", 10_000, harness.Run.Cancellation.Token);

        // The timer is armed from TimeProvider.CreateTimer, so only monotonic time (Advance) elapses the wait. The
        // recorded deadline is a plain string of the wall clock at request time and is never read back by the host.
        Assert.False(wait.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Within(wait);

        Assert.Equal(["wait.requested", "wait.elapsed"], harness.WaitKinds);
    }

    [Fact]
    public async Task Cancelling_during_a_wait_settles_as_cancelled_and_ends_the_node()
    {
        using var harness = Create(WaitWorkflow(60_000));
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, harness.Run.Cancellation.Token);

        harness.Run.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(["wait.requested", "wait.cancelled"], harness.WaitKinds);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task When_the_timer_completes_before_cancellation_elapse_wins_and_no_cancelled_settlement_is_recorded()
    {
        using var harness = Create(WaitWorkflow(60_000));
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, harness.Run.Cancellation.Token);

        clock.Advance(TimeSpan.FromMinutes(1));
        harness.Run.Cancel();
        // The node either returns before the cancel is observed or throws at the reacquire boundary; the wait is
        // elapsed in both cases, and the runner ends the run cancelled at its next boundary.
        try { await Within(wait); } catch (OperationCanceledException) { }

        Assert.Equal(["wait.requested", "wait.elapsed"], harness.WaitKinds);
    }

    [Fact]
    public async Task When_cancellation_comes_first_cancelled_wins_even_if_time_then_advances_past_the_deadline()
    {
        using var harness = Create(WaitWorkflow(60_000));
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, harness.Run.Cancellation.Token);

        harness.Run.Cancel();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));

        Assert.Equal(["wait.requested", "wait.cancelled"], harness.WaitKinds);
    }

    [Fact]
    public async Task A_host_shutdown_settles_as_interrupted_and_is_distinct_from_a_user_cancel()
    {
        using var harness = Create(WaitWorkflow(60_000));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(harness.Run.Cancellation.Token);
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, shutdown.Token);

        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));

        Assert.Equal(["wait.requested", "wait.interrupted"], harness.WaitKinds);
    }

    [Fact]
    public async Task Elapse_beats_a_shutdown_that_arrives_after_the_timer_completed_and_shutdown_beats_a_later_elapse()
    {
        using var elapseFirst = Create(WaitWorkflow(5_000));
        using var late = CancellationTokenSource.CreateLinkedTokenSource(elapseFirst.Run.Cancellation.Token);
        var (wait, _) = await BeginWait(elapseFirst, "wait1", 5_000, late.Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        await late.CancelAsync();
        try { await Within(wait); } catch (OperationCanceledException) { }
        Assert.Equal(["wait.requested", "wait.elapsed"], elapseFirst.WaitKinds);

        using var shutdownFirst = Create(WaitWorkflow(5_000));
        using var early = CancellationTokenSource.CreateLinkedTokenSource(shutdownFirst.Run.Cancellation.Token);
        var (second, _) = await BeginWait(shutdownFirst, "wait1", 5_000, early.Token);
        await early.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(second));
        Assert.Equal(["wait.requested", "wait.interrupted"], shutdownFirst.WaitKinds);
    }

    [Fact]
    public async Task Exactly_one_settlement_is_recorded_however_many_stimuli_arrive()
    {
        using var harness = Create(WaitWorkflow(2_000));
        var (wait, _) = await BeginWait(harness, "wait1", 2_000, harness.Run.Cancellation.Token);

        harness.Run.Cancel();
        harness.Run.Cancel();
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));
        harness.Run.Cancel();

        Assert.Single(harness.Events, evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted);
    }

    [Fact]
    public async Task Pause_during_a_wait_is_acknowledged_at_once_never_stops_the_clock_and_holds_the_node_after_it_elapses()
    {
        using var harness = Create(WaitWorkflow(30_000));
        var (wait, _) = await BeginWait(harness, "wait1", 30_000, harness.Run.Cancellation.Token);

        harness.Run.RequestPause();

        var paused = Assert.Single(harness.Events, evt => evt.Kind == ExecutionEventKinds.Paused);
        Assert.Equal("wait1", paused.NodeId);
        Assert.Equal(1, harness.Slot.CurrentCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await Recorded(harness, ExecutionEventKinds.WaitElapsed);
        Assert.False(wait.IsCompleted, "A paused run holds at the boundary after the wait; it does not complete the node.");
        Assert.Equal(1, harness.Slot.CurrentCount);

        harness.Run.Resume();
        await Within(wait);

        Assert.Equal(0, harness.Slot.CurrentCount);
        Assert.Single(harness.Events, evt => evt.Kind == ExecutionEventKinds.Paused);
        Assert.Equal(["wait.requested", "wait.elapsed"], harness.WaitKinds);
    }

    [Fact]
    public async Task Cancelling_a_paused_run_during_a_wait_settles_as_cancelled()
    {
        using var harness = Create(WaitWorkflow(30_000));
        var (wait, _) = await BeginWait(harness, "wait1", 30_000, harness.Run.Cancellation.Token);
        harness.Run.RequestPause();

        harness.Run.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));

        Assert.Equal(["wait.requested", "wait.cancelled"], harness.WaitKinds);
    }

    [Fact]
    public async Task A_breakpoint_on_the_wait_node_is_not_hit_a_second_time_when_the_wait_reacquires_its_slot()
    {
        using var harness = Create(WaitWorkflow(1_000));
        harness.Run.SetBreakpoints(["wait1"]);
        var boundary = harness.Run.WaitForBoundaryAsync("wait1", harness.Run.Cancellation.Token).AsTask();
        await Recorded(harness, ExecutionEventKinds.BreakpointHit);
        Assert.False(boundary.IsCompleted);
        harness.Run.Resume();
        await Within(boundary);
        var wait = harness.Run.WaitAsync(Request("wait1", 1_000), harness.Run.Cancellation.Token).AsTask();

        clock.Advance(TimeSpan.FromSeconds(1));
        await Within(wait);

        Assert.Single(harness.Events, evt => evt.Kind == ExecutionEventKinds.BreakpointHit);
    }

    [Fact]
    public async Task An_over_bound_request_is_rejected_without_a_wait_event_or_a_slot_change()
    {
        using var harness = Create(WaitWorkflow(120_000), new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), 4));
        await harness.Run.WaitForBoundaryAsync("wait1", harness.Run.Cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Run.WaitAsync(Request("wait1", 120_000), harness.Run.Cancellation.Token).AsTask());

        Assert.Empty(harness.WaitKinds);
        Assert.Equal(0, harness.Slot.CurrentCount);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task The_cumulative_budget_is_enforced_across_waits_of_one_run()
    {
        using var harness = Create(WaitWorkflow(60_000, 60_000, 60_000), new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), 4));
        var token = harness.Run.Cancellation.Token;
        foreach (var node in new[] { "wait1", "wait2" })
        {
            var (wait, _) = await BeginWait(harness, node, 60_000, token);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Within(wait);
        }
        await harness.Run.WaitForBoundaryAsync("wait3", token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Run.WaitAsync(Request("wait3", 60_000), token).AsTask());

        Assert.Equal(["wait.requested", "wait.elapsed", "wait.requested", "wait.elapsed"], harness.WaitKinds);
    }

    [Fact]
    public async Task The_host_derives_the_duration_from_the_captured_definition_not_from_the_runner_request()
    {
        using var harness = Create(WaitWorkflow(1_000));
        await harness.Run.WaitForBoundaryAsync("wait1", harness.Run.Cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Run.WaitAsync(Request("wait1", 999), harness.Run.Cancellation.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Run.WaitAsync(Request("input", 1_000), harness.Run.Cancellation.Token).AsTask());

        Assert.Empty(harness.WaitKinds);
    }

    [Fact]
    public async Task Disposing_the_run_mid_wait_settles_it_as_interrupted_in_the_live_journal()
    {
        using var harness = Create(WaitWorkflow(60_000));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(harness.Run.Cancellation.Token);
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, shutdown.Token);

        harness.Run.Dispose();
        Assert.Equal(["wait.requested", "wait.interrupted"], harness.WaitKinds);

        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => Within(wait));
        Assert.Equal(["wait.requested", "wait.interrupted"], harness.WaitKinds);
        Assert.Empty(WorkflowWorkEvidence.Read(harness.Store.Get(harness.Header.RunId)!).UnresolvedWork.Where(work => work.CallId is not null));
    }

    [Fact]
    public async Task A_journal_failure_during_settlement_never_masks_the_cancellation_and_leaves_the_request_unsettled()
    {
        using var harness = Create(WaitWorkflow(60_000));
        var (wait, _) = await BeginWait(harness, "wait1", 60_000, harness.Run.Cancellation.Token);

        harness.Store.FailAppends = true;
        harness.Run.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));
        harness.Store.FailAppends = false;
        // Documented remainder: with an unwritable journal the request stays unsettled and reconciles as unresolved.
        Assert.Equal(["wait.requested"], harness.WaitKinds);
    }

    [Fact]
    public async Task Cancelling_during_the_post_elapse_pause_hold_records_no_second_settlement()
    {
        using var harness = Create(WaitWorkflow(30_000));
        var (wait, _) = await BeginWait(harness, "wait1", 30_000, harness.Run.Cancellation.Token);
        harness.Run.RequestPause();
        clock.Advance(TimeSpan.FromSeconds(30));
        await Recorded(harness, ExecutionEventKinds.WaitElapsed);

        harness.Run.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(wait));
        Assert.Equal(["wait.requested", "wait.elapsed"], harness.WaitKinds);
    }

    [Fact]
    public void Stepping_is_rejected_for_a_workflow_with_a_wait()
    {
        using var harness = Create(WaitWorkflow(1_000));
        harness.Run.RequestPause();

        Assert.Throws<InvalidOperationException>(harness.Run.Step);
    }

    // ---- runtime ----------------------------------------------------------------------------------------------

    private SignalingStore? runtimeStore;
    private SignalingStore RuntimeStore => runtimeStore ??= new(new FileRunJournal(directory));

    private LocalWorkflowRuntime Runtime(WorkflowWaitOptions? options = null) =>
        new(RuntimeStore, NullLogger<LocalWorkflowRuntime>.Instance, null, null, null, null,
            options ?? new WorkflowWaitOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 2), clock);

    private Task<RunSnapshot> WaitFor(LocalWorkflowRuntime runtime, Guid id, Func<RunSnapshot, bool> condition) =>
        RuntimeStore.WhenAsync(id, condition);

    [Fact]
    public async Task A_run_waits_without_holding_the_slot_then_completes_when_the_clock_advances()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var waiting = await runtime.StartRunAsync(WaitWorkflow(60_000), "abc");
            await WaitFor(runtime, waiting.Header.RunId, run => run.Events.Any(evt => evt.Kind == ExecutionEventKinds.WaitRequested));

            // The waiting run released the shared execution slot, so an ordinary run finishes meanwhile.
            var other = await runtime.StartRunAsync(PlainWorkflow, "other");
            Assert.Equal("completed", (await WaitFor(runtime, other.Header.RunId, run => run.IsTerminal)).Status);
            Assert.Equal("running", (await runtime.GetRunAsync(waiting.Header.RunId))!.Status);

            clock.Advance(TimeSpan.FromMinutes(1));
            var finished = await WaitFor(runtime, waiting.Header.RunId, run => run.IsTerminal);

            Assert.Equal("completed", finished.Status);
            Assert.Equal("abc", finished.Events[^1].Data);
            var kinds = finished.Events.Select(evt => evt.Kind).ToArray();
            var started = Array.FindIndex(kinds, kind => kind == ExecutionEventKinds.WaitRequested);
            Assert.Equal(ExecutionEventKinds.NodeStarted, kinds[started - 1]);
            Assert.Equal(ExecutionEventKinds.WaitElapsed, kinds[started + 1]);
            Assert.Equal(ExecutionEventKinds.NodeCompleted, kinds[started + 2]);
            Assert.Equal(finished.Events.Length, finished.Events.Select(evt => evt.Sequence).Distinct().Count());
            Assert.Empty(WorkflowWorkEvidence.Read(finished).UnresolvedWork);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancelling_a_waiting_run_records_wait_cancelled_and_a_cancelled_run()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(WaitWorkflow(60_000), "abc");
            await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.Events.Any(evt => evt.Kind == ExecutionEventKinds.WaitRequested));

            await runtime.CancelRunAsync(run.Header.RunId);
            var finished = await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.IsTerminal);

            Assert.Equal("cancelled", finished.Status);
            Assert.Contains(finished.Events, evt => evt.Kind == ExecutionEventKinds.WaitCancelled);
            Assert.DoesNotContain(finished.Events, evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitInterrupted);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_clean_host_shutdown_during_a_wait_records_wait_interrupted_and_leaves_no_unresolved_wait()
    {
        var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        var run = await runtime.StartRunAsync(WaitWorkflow(60_000), "abc");
        await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.Events.Any(evt => evt.Kind == ExecutionEventKinds.WaitRequested));

        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();

        var stored = new FileRunJournal(directory).Get(run.Header.RunId)!;
        Assert.Contains(stored.Events, evt => evt.Kind == ExecutionEventKinds.WaitInterrupted);
        Assert.DoesNotContain(stored.Events, evt => evt.Kind == ExecutionEventKinds.WaitCancelled);
        Assert.True(stored.IsTerminal);
        Assert.DoesNotContain(WorkflowWorkEvidence.Read(stored).UnresolvedWork, work => work.CallId is not null);
    }

    [Fact]
    public async Task An_over_bound_definition_is_rejected_at_admission_not_clamped_and_creates_no_run()
    {
        using var runtime = Runtime(new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), 2));
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var perWait = await Assert.ThrowsAsync<WorkflowWaitAdmissionException>(() => runtime.StartRunAsync(WaitWorkflow(61_000), "abc"));
            Assert.Contains(perWait.Issues, issue => issue.Code == "wait.bound");
            var budget = await Assert.ThrowsAsync<WorkflowWaitAdmissionException>(() => runtime.StartRunAsync(WaitWorkflow(60_000, 60_000, 60_000), "abc"));
            Assert.Contains(budget.Issues, issue => issue.Code == "wait.budget");

            Assert.Empty(await runtime.ListRunsAsync());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task The_waiting_run_cap_rejects_an_extra_wait_run_but_never_blocks_other_work_and_frees_on_completion()
    {
        using var runtime = Runtime(new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 1));
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var first = await runtime.StartRunAsync(WaitWorkflow(60_000), "one");
            await WaitFor(runtime, first.Header.RunId, snapshot => snapshot.Events.Any(evt => evt.Kind == ExecutionEventKinds.WaitRequested));

            var refused = await runtime.StartRunAsync(WaitWorkflow(60_000), "two");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("at most 1 concurrent runs that contain a Wait node", refused.Events[^1].Data);
            var plain = await runtime.StartRunAsync(PlainWorkflow, "plain");
            Assert.Equal("completed", (await WaitFor(runtime, plain.Header.RunId, snapshot => snapshot.IsTerminal)).Status);

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("completed", (await WaitFor(runtime, first.Header.RunId, snapshot => snapshot.IsTerminal)).Status);
            await WaitFor(runtime, first.Header.RunId, _ => true);
            var third = await StartWhenCapacityFrees(runtime);
            Assert.NotEqual("failed", third.Status);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    private async Task<RunSnapshot> StartWhenCapacityFrees(LocalWorkflowRuntime runtime)
    {
        // The finished run releases its capacity when its continuation unwinds just after its terminal event; no recorder
        // signal exists for that, so retry the admission with a bounded yield loop (no timer sleeps).
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var attempt = await runtime.StartRunAsync(WaitWorkflow(60_000), "three");
            if (attempt.Status != "failed") return attempt;
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task A_process_restart_with_an_unsettled_wait_reconciles_as_unresolved_non_resumable_work()
    {
        var definition = WaitWorkflow(60_000);
        var revision = WorkflowRevision.Create(definition, "test");
        var store = new FileRunJournal(directory);
        var run = store.Create(revision, "abc");
        var invocation = Guid.NewGuid();
        var waitId = Guid.NewGuid();
        long sequence = 0;
        void Add(string kind, string node, Guid? invocationId = null, string? data = null) =>
            store.Append(new(run.Header.RunId, revision.RevisionId, ++sequence, Start, kind, node, invocationId, Data: data));
        Add(ExecutionEventKinds.Started, null!);
        Add(ExecutionEventKinds.NodeStarted, "wait1", invocation);
        Add(ExecutionEventKinds.WaitRequested, "wait1", invocation, JsonSerializer.Serialize(new { waitId }));

        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        await runtime.StopAsync(CancellationToken.None);

        var restarted = new FileRunJournal(directory).Get(run.Header.RunId)!;
        Assert.Equal("interrupted", restarted.Status);
        Assert.DoesNotContain(restarted.Events, evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted);
        var reconciliation = WorkflowRunReconciliation.Assess(restarted, null);
        Assert.Equal(RunRecoveryDisposition.UnresolvedWork, reconciliation.Disposition);
        Assert.False(reconciliation.CanResume);
        Assert.Contains(reconciliation.UnresolvedWork, work => work.Kind == UnresolvedWorkKind.Node && work.CallId == waitId &&
            work.NodeId == "wait1" && work.InvocationId == invocation);
        Assert.True(Enum.IsDefined(reconciliation.UnresolvedWork[0].Kind));
    }

    [Fact]
    public void A_wait_graph_is_rejected_by_the_checkpoint_allowlist()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ValidateCheckpointGraph(WaitWorkflow(1_000)));

        Assert.Contains("Wait nodes", error.Message, StringComparison.Ordinal);
    }
}
