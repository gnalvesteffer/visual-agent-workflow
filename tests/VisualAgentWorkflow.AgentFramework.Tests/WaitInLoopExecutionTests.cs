using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Wait in a sequential Loop or For Each body (Decision 0081, 2026-11-25). Every duration is driven by
/// <see cref="ManualTimeProvider"/>; the tests await recorded journal events and never sleep for a wait.
/// </summary>
public sealed class WaitInLoopExecutionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-waitloop-" + Guid.NewGuid());
    private readonly ManualTimeProvider clock = new(Start);
    private readonly WorkflowWaitOptions options = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 2);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static NodeDefinition Wait(long milliseconds) => new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) };

    private static ConnectionDefinition Exec(string from, string fromPin, string to, string toPin) => new(new(from, fromPin), new(to, toPin), "execution");

    /// <summary>input -> loop; body -> wait -> set -> get -> feedback; done -> output. The input "continue" runs every iteration.</summary>
    private static WorkflowDefinition LoopWorkflow(long milliseconds, int iterations)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), iterations) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["wait"] = Wait(milliseconds),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["set-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["output-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = Exec("input", "then", "loop", "execute"),
            ["loop-body"] = Exec("loop", "body", "body", "execute"),
            ["body-wait"] = Exec("body", "then", "wait", "execute"),
            ["wait-set"] = Exec("wait", "then", "set", "execute"),
            ["set-get"] = Exec("set", "then", "get", "execute"),
            ["get-loop"] = Exec("get", "then", "loop", "feedback"),
            ["loop-output"] = Exec("loop", "done", "output", "execute")
        };
        return new(WorkflowSchemaVersions.CurrentAuthoringSchema, "wait-in-loop", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty.Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    /// <summary>input -> items -> For Each; body -> wait -> step -> feedback; completed -> output.</summary>
    private static WorkflowDefinition ForEachWorkflow(long milliseconds, bool breakAfterWait = false)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]") },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["wait"] = Wait(milliseconds),
            ["step"] = new(NodeTypes.SequenceReverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = new(new("items", "out"), new("each", "array"), "data"),
            ["item-step"] = new(new("each", "item"), new("step", "in"), "data"),
            ["result"] = new(new("step", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Exec("input", "then", "each", "execute"),
            ["body-start"] = Exec("each", "body", "body", "execute"),
            ["body-wait"] = Exec("body", "then", "wait", "execute"),
            ["wait-step"] = Exec("wait", "then", "step", "execute"),
            ["return"] = Exec("step", "then", "each", "feedback"),
            ["done"] = Exec("each", "completed", "output", "execute")
        };
        if (breakAfterWait)
        {
            nodes["is-b"] = new(NodeTypes.StringEquals, "1.0.0");
            nodes["b-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("b") };
            nodes["route"] = new(NodeTypes.BooleanBranch, "1.0.0");
            nodes["break"] = new(NodeTypes.Break, "1.0.0");
            data["item-eq"] = new(new("each", "item"), new("is-b", "left"), "data");
            data["text-eq"] = new(new("b-text", "out"), new("is-b", "right"), "data");
            data["eq-route"] = new(new("is-b", "out"), new("route", "condition"), "data");
            execution["wait-step"] = Exec("wait", "then", "route", "execute");
            execution["route-break"] = Exec("route", "true", "break", "execute");
            execution["route-step"] = Exec("route", "false", "step", "execute");
        }
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-in-for-each", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    private sealed class SignalingStore(IWorkflowRunStore inner) : IWorkflowRunStore
    {
        private readonly object gate = new();
        private readonly List<(Guid RunId, Func<RunSnapshot, bool> Condition, TaskCompletionSource<RunSnapshot> Signal)> waiters = [];

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
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

    private SignalingStore? store;
    private SignalingStore Store => store ??= new(new FileRunJournal(directory));

    private LocalWorkflowRuntime Runtime(WorkflowWaitOptions? custom = null) =>
        new(Store, NullLogger<LocalWorkflowRuntime>.Instance, null, null, null, null, custom ?? options, clock);

    private static int Count(RunSnapshot run, string kind) => run.Events.Count(evt => evt.Kind == kind);

    private Task<RunSnapshot> Requested(Guid runId, int count) => Store.WhenAsync(runId, run => Count(run, ExecutionEventKinds.WaitRequested) >= count);

    /// <summary>Advances the fake clock past one wait once its request is recorded, so no timer is missed.</summary>
    private async Task Elapse(Guid runId, int iteration, long milliseconds)
    {
        await Requested(runId, iteration);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static JsonElement Payload(ExecutionEvent evt) => JsonDocument.Parse(evt.Data!).RootElement;

    private static void AssertSettledExactlyOnce(RunSnapshot run)
    {
        var requests = run.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        var settled = run.Events.Where(evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted)
            .Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        Assert.Equal(requests.Order(), settled.Order());
        Assert.Equal(requests.Length, requests.Distinct().Count());
    }

    [Fact]
    public async Task A_loop_waits_once_per_iteration_with_distinct_wait_ids_and_the_loop_iteration_in_the_evidence()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(LoopWorkflow(60_000, 3), "continue");
            for (var iteration = 1; iteration <= 3; iteration++) await Elapse(run.Header.RunId, iteration, 60_000);
            var finished = await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.IsTerminal);

            Assert.Equal("completed", finished.Status);
            var requests = finished.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).ToArray();
            var elapsed = finished.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitElapsed).ToArray();
            Assert.Equal(3, requests.Length);
            Assert.Equal([1, 2, 3], requests.Select(evt => Payload(evt).GetProperty("loopIteration").GetInt32()));
            Assert.Equal([1, 2, 3], elapsed.Select(evt => Payload(evt).GetProperty("loopIteration").GetInt32()));
            Assert.Equal(["loop/1", "loop/2", "loop/3"], requests.Select(evt => evt.ScopeId));
            Assert.Equal(requests.Select(evt => evt.ScopeId), elapsed.Select(evt => evt.ScopeId));
            Assert.Equal(3, requests.Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).Distinct().Count());
            Assert.Equal(3, requests.Select(evt => evt.InvocationId).Distinct().Count());
            Assert.Equal([60_000L, 120_000L, 180_000L], requests.Select(evt => Payload(evt).GetProperty("bound").GetProperty("cumulativeRequestedMilliseconds").GetInt64()));
            AssertSettledExactlyOnce(finished);
            Assert.Empty(WorkflowWorkEvidence.Read(finished).UnresolvedWork);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_for_each_waits_once_per_item()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(ForEachWorkflow(1_000), "abc");
            for (var item = 1; item <= 3; item++) await Elapse(run.Header.RunId, item, 1_000);
            var finished = await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.IsTerminal);

            Assert.Equal("completed", finished.Status);
            var requests = finished.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).ToArray();
            Assert.Equal([1, 2, 3], requests.Select(evt => Payload(evt).GetProperty("loopIteration").GetInt32()));
            Assert.Equal(["each/1", "each/2", "each/3"], requests.Select(evt => evt.ScopeId));
            AssertSettledExactlyOnce(finished);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_break_after_a_wait_in_a_for_each_ends_the_loop_after_the_second_item_with_every_wait_settled_once()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(ForEachWorkflow(1_000, breakAfterWait: true), "abc");
            await Elapse(run.Header.RunId, 1, 1_000);
            await Elapse(run.Header.RunId, 2, 1_000);
            var finished = await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.IsTerminal);

            Assert.Equal(2, Count(finished, ExecutionEventKinds.WaitRequested));
            AssertSettledExactlyOnce(finished);
            Assert.Empty(WorkflowWorkEvidence.Read(finished).UnresolvedWork);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancelling_during_a_mid_loop_wait_settles_that_wait_exactly_once_and_starts_no_more()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(LoopWorkflow(60_000, 3), "continue");
            await Elapse(run.Header.RunId, 1, 60_000);
            await Requested(run.Header.RunId, 2);

            await runtime.CancelRunAsync(run.Header.RunId);
            var finished = await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.IsTerminal);
            clock.Advance(TimeSpan.FromHours(1));

            Assert.Equal("cancelled", finished.Status);
            Assert.Equal(2, Count(finished, ExecutionEventKinds.WaitRequested));
            Assert.Equal(1, Count(finished, ExecutionEventKinds.WaitElapsed));
            Assert.Equal(1, Count(finished, ExecutionEventKinds.WaitCancelled));
            Assert.Equal(0, Count(finished, ExecutionEventKinds.WaitInterrupted));
            AssertSettledExactlyOnce(finished);
            Assert.Equal(2, Payload(finished.Events.Single(evt => evt.Kind == ExecutionEventKinds.WaitCancelled)).GetProperty("loopIteration").GetInt32());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_clean_shutdown_mid_loop_wait_settles_as_interrupted_once()
    {
        var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        var run = await runtime.StartRunAsync(LoopWorkflow(60_000, 3), "continue");
        await Elapse(run.Header.RunId, 1, 60_000);
        await Requested(run.Header.RunId, 2);

        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();

        var stored = new FileRunJournal(directory).Get(run.Header.RunId)!;
        Assert.Equal(1, Count(stored, ExecutionEventKinds.WaitInterrupted));
        AssertSettledExactlyOnce(stored);
    }

    [Fact]
    public async Task Pausing_during_a_mid_loop_wait_holds_after_it_elapses_and_resume_continues_with_the_next_iteration()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(LoopWorkflow(60_000, 3), "continue");
            await Elapse(run.Header.RunId, 1, 60_000);
            await Requested(run.Header.RunId, 2);

            await runtime.PauseRunAsync(run.Header.RunId);
            await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.Events.Any(evt => evt.Kind == ExecutionEventKinds.Paused));
            clock.Advance(TimeSpan.FromMinutes(1));
            var held = await Store.WhenAsync(run.Header.RunId, snapshot => Count(snapshot, ExecutionEventKinds.WaitElapsed) == 2);

            Assert.Equal(2, Count(held, ExecutionEventKinds.WaitRequested));
            Assert.False(held.IsTerminal);

            await runtime.ResumeRunAsync(run.Header.RunId);
            await Elapse(run.Header.RunId, 3, 60_000);
            var finished = await Store.WhenAsync(run.Header.RunId, snapshot => snapshot.IsTerminal);

            Assert.Equal("completed", finished.Status);
            Assert.Equal(3, Count(finished, ExecutionEventKinds.WaitRequested));
            AssertSettledExactlyOnce(finished);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Admission_rejects_a_loop_whose_iterations_times_duration_exceed_the_host_budget()
    {
        using var runtime = Runtime(new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(2_999), 2));
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowWaitAdmissionException>(() => runtime.StartRunAsync(LoopWorkflow(1_000, 3), "continue"));
            Assert.Contains(error.Issues, issue => issue.Code == "wait.budget");
            Assert.Empty(await runtime.ListRunsAsync());

            var accepted = await runtime.StartRunAsync(LoopWorkflow(1_000, 2), "continue");
            Assert.NotNull(accepted);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task The_runtime_backstop_fails_the_wait_beyond_the_cumulative_budget_when_admission_is_bypassed()
    {
        var definition = LoopWorkflow(1_000, 3);
        var journal = new SignalingStore(new FileRunJournal(directory));
        var header = journal.Create(WorkflowRevision.Create(definition, "test"), "continue").Header;
        using var slot = new SemaphoreSlim(1, 1);
        using var run = new ControlledRun(journal, header, slot, clock, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2));
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await run.WaitForBoundaryAsync("wait", run.Cancellation.Token);
            var wait = run.WaitAsync(new("wait", Guid.NewGuid(), TimeSpan.FromSeconds(1), iteration + 1, $"loop/{iteration}"), run.Cancellation.Token).AsTask();
            clock.Advance(TimeSpan.FromSeconds(1));
            await wait.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await run.WaitForBoundaryAsync("wait", run.Cancellation.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.WaitAsync(new("wait", Guid.NewGuid(), TimeSpan.FromSeconds(1), 2, "loop/2"), run.Cancellation.Token).AsTask());

        Assert.Contains("budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, Count(journal.Get(header.RunId)!, ExecutionEventKinds.WaitRequested));
    }

    [Fact]
    public async Task A_top_level_wait_payload_has_no_loop_fields_and_a_loop_wait_adds_only_them()
    {
        // Existing golden shape: request/settlement of a top-level wait carry no loopIteration and no scope.
        var definition = LoopWorkflow(1_000, 2);
        var header = Store.Create(WorkflowRevision.Create(definition, "test"), "continue").Header;
        using var slot = new SemaphoreSlim(1, 1);
        using var run = new ControlledRun(Store, header, slot, clock, options);
        await run.WaitForBoundaryAsync("wait", run.Cancellation.Token);
        var top = run.WaitAsync(new("wait", Guid.NewGuid(), TimeSpan.FromSeconds(1)), run.Cancellation.Token).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        await top.WaitAsync(TimeSpan.FromSeconds(10));

        var events = Store.Get(header.RunId)!.Events.Where(evt => evt.Kind.StartsWith("wait.", StringComparison.Ordinal)).ToArray();
        Assert.All(events, evt => Assert.Null(evt.ScopeId));
        Assert.All(events, evt => Assert.False(Payload(evt).TryGetProperty("loopIteration", out _)));
        Assert.Equal(["waitId", "nodeId", "durationMilliseconds", "deadlineUtc", "bound"],
            Payload(events[0]).EnumerateObject().Select(property => property.Name));
        Assert.Equal(["waitId", "nodeId", "durationMilliseconds", "elapsedMilliseconds", "reason"],
            Payload(events[1]).EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Node_stepping_and_checkpoint_recovery_still_reject_a_loop_containing_a_wait()
    {
        var definition = LoopWorkflow(1_000, 2);

        Assert.False(WorkflowDebugging.SupportsNodeStepping(definition));
        Assert.Contains("Wait nodes", Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ValidateCheckpointGraph(definition)).Message, StringComparison.Ordinal);
        Assert.Contains("Wait nodes", Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ValidateCheckpointGraph(ForEachWorkflow(1_000))).Message, StringComparison.Ordinal);
    }
}
