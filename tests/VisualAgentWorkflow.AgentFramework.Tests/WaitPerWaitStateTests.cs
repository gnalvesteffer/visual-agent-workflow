using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0081, "Wait per-wait state": <see cref="ControlledRun"/> keeps pending waits keyed by waitId behind an internal
/// concurrency limit that defaults to 1. No placement that could produce concurrent waits is enabled, so these tests drive
/// the gate directly with <see cref="ManualTimeProvider"/> and never sleep. The gate calls themselves record their events
/// synchronously under the run lock, so the journal can be read right after each call.
/// </summary>
public sealed class WaitPerWaitStateTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-perwait-" + Guid.NewGuid());
    private readonly ManualTimeProvider clock = new(Start);
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly FileRunJournal journal;
    private readonly RunHeader header;

    public WaitPerWaitStateTests()
    {
        journal = new FileRunJournal(directory);
        header = journal.Create(WorkflowRevision.Create(ThreeWaits(), "test"), "x").Header;
    }

    public void Dispose()
    {
        slot.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static ConnectionDefinition Exec(string from, string fromPin, string to, string toPin) => new(new(from, fromPin), new(to, toPin), "execution");

    /// <summary>input -> wait1 -> wait2 -> wait3 -> output; the placement is irrelevant because the gate is driven directly.</summary>
    private static WorkflowDefinition ThreeWaits() => new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "per-wait", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["wait1"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(1_000) },
            ["wait2"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(1_000) },
            ["wait3"] = new(NodeTypes.Wait, "1.0.0") { Wait = new(1_000) },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("value", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["a"] = Exec("input", "then", "wait1", "execute"),
            ["b"] = Exec("wait1", "then", "wait2", "execute"),
            ["c"] = Exec("wait2", "then", "wait3", "execute"),
            ["d"] = Exec("wait3", "then", "output", "execute")
        }.ToImmutableSortedDictionary()
    };

    private ControlledRun Run(int limit, WorkflowWaitOptions? options = null) =>
        new(journal, header, slot, clock, options ?? new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 2)) { ConcurrentWaitLimit = limit };

    private static WorkflowWaitRequest Request(string nodeId, string? scope = null, int? iteration = null) =>
        new(nodeId, Guid.NewGuid(), TimeSpan.FromSeconds(1), iteration, scope);

    private RunSnapshot Snapshot() => journal.Get(header.RunId)!;
    private static int Count(RunSnapshot run, string kind) => run.Events.Count(evt => evt.Kind == kind);
    private static JsonElement Payload(ExecutionEvent evt) => JsonDocument.Parse(evt.Data!).RootElement;

    private static void AssertSettledExactlyOnce(RunSnapshot run)
    {
        var requested = run.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        var settled = run.Events.Where(evt => evt.Kind is ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted)
            .Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).ToArray();
        Assert.Equal(requested.Order(), settled.Order());
        Assert.Equal(requested.Length, requested.Distinct().Count());
    }

    [Fact]
    public async Task The_default_limit_keeps_a_second_pending_wait_rejected_and_the_first_settles_once()
    {
        using var run = Run(limit: 1);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();
        await run.WaitForBoundaryAsync("wait2", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask());

        Assert.Equal("A wait is already pending.", error.Message);
        Assert.Equal(1, Count(Snapshot(), ExecutionEventKinds.WaitRequested));
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        AssertSettledExactlyOnce(Snapshot());
    }

    [Fact]
    public async Task With_a_limit_of_two_each_wait_has_its_own_id_scope_and_settlement()
    {
        using var run = Run(limit: 2);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        await run.WaitForBoundaryAsync("wait2", CancellationToken.None);
        var first = run.WaitAsync(Request("wait1", "fork/branch-a"), run.Cancellation.Token).AsTask();
        var second = run.WaitAsync(Request("wait2", "sequence/out-2", iteration: 3), run.Cancellation.Token).AsTask();
        await run.WaitForBoundaryAsync("wait3", CancellationToken.None);
        var third = Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Request("wait3"), run.Cancellation.Token).AsTask());
        Assert.Equal("A wait is already pending.", (await third).Message);

        var requests = Snapshot().Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitRequested).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(["fork/branch-a", "sequence/out-2"], requests.Select(evt => evt.ScopeId));
        Assert.False(Payload(requests[0]).TryGetProperty("loopIteration", out _));
        Assert.Equal(3, Payload(requests[1]).GetProperty("loopIteration").GetInt32());
        Assert.Equal(2, requests.Select(evt => Payload(evt).GetProperty("waitId").GetGuid()).Distinct().Count());
        Assert.Equal([1_000L, 2_000L], requests.Select(evt => Payload(evt).GetProperty("bound").GetProperty("cumulativeRequestedMilliseconds").GetInt64()));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = Snapshot();
        Assert.Equal(2, Count(snapshot, ExecutionEventKinds.WaitElapsed));
        Assert.Equal(["fork/branch-a", "sequence/out-2"], snapshot.Events.Where(evt => evt.Kind == ExecutionEventKinds.WaitElapsed).Select(evt => evt.ScopeId).Order());
        AssertSettledExactlyOnce(snapshot);
    }

    [Fact]
    public async Task The_budget_is_reserved_atomically_with_the_registration_across_concurrent_waits()
    {
        using var run = Run(limit: 2, new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1_500), 2));
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        await run.WaitForBoundaryAsync("wait2", CancellationToken.None);
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask());

        Assert.Contains("budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, Count(Snapshot(), ExecutionEventKinds.WaitRequested));
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        // The elapsed wait keeps its reservation: the rejected request is still over the budget afterwards.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask());
        Assert.Contains("budget", again.Message, StringComparison.Ordinal);
        AssertSettledExactlyOnce(Snapshot());
    }

    [Fact]
    public async Task Cancel_settles_every_pending_wait_as_cancelled_exactly_once()
    {
        using var run = Run(limit: 2);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        await run.WaitForBoundaryAsync("wait2", CancellationToken.None);
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();
        var second = run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask();

        run.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));

        var snapshot = Snapshot();
        Assert.Equal(2, Count(snapshot, ExecutionEventKinds.WaitCancelled));
        AssertSettledExactlyOnce(snapshot);
        Assert.Empty(WorkflowWorkEvidence.Read(snapshot).UnresolvedWork);
    }

    [Fact]
    public async Task Disposing_the_run_settles_every_pending_wait_as_interrupted_exactly_once()
    {
        var run = Run(limit: 2);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        await run.WaitForBoundaryAsync("wait2", CancellationToken.None);
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();
        var second = run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask();

        run.Dispose();

        var snapshot = Snapshot();
        Assert.Equal(2, Count(snapshot, ExecutionEventKinds.WaitInterrupted));
        AssertSettledExactlyOnce(snapshot);
        Assert.Empty(WorkflowWorkEvidence.Read(snapshot).UnresolvedWork);
        // The orphaned timers were never cancelled, so advancing the clock must not add a second settlement.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Count(Snapshot(), ExecutionEventKinds.WaitInterrupted));
        Assert.Equal(0, Count(Snapshot(), ExecutionEventKinds.WaitElapsed));
        GC.KeepAlive(first); GC.KeepAlive(second);
    }

    [Fact]
    public async Task Pause_is_not_acknowledged_while_a_sibling_node_runs_even_when_other_waits_released_their_slots()
    {
        using var run = Run(limit: 2);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);   // branch A
        await run.WaitForBranchStartAsync("wait2", null, CancellationToken.None);   // branch B
        await run.WaitForBranchStartAsync("output", null, CancellationToken.None);  // sibling C keeps executing
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();
        var second = run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask();

        run.RequestPause();

        // A and B are waiting, but C is still in flight: the slot is held and nothing may claim the run is paused.
        Assert.Equal(0, Count(Snapshot(), ExecutionEventKinds.Paused));
        Assert.Equal(0, slot.CurrentCount);

        var siblingBoundary = run.WaitForBoundaryAsync("wait3", CancellationToken.None).AsTask();
        Assert.Equal(1, Count(Snapshot(), ExecutionEventKinds.Paused));
        Assert.Equal(1, slot.CurrentCount);
        Assert.False(siblingBoundary.IsCompleted);

        // The clock keeps running while paused: both waits settle, then hold at the reacquire boundary.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Count(Snapshot(), ExecutionEventKinds.WaitElapsed));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        run.Resume();
        await Task.WhenAll(siblingBoundary, first, second).WaitAsync(TimeSpan.FromSeconds(10));
        AssertSettledExactlyOnce(Snapshot());
        Assert.Equal(1, Count(Snapshot(), ExecutionEventKinds.Paused));
    }

    [Fact]
    public async Task Pause_is_acknowledged_when_the_last_in_flight_node_enters_a_wait()
    {
        using var run = Run(limit: 2);
        await run.WaitForBoundaryAsync("wait1", CancellationToken.None);
        await run.WaitForBranchStartAsync("wait2", null, CancellationToken.None);
        var first = run.WaitAsync(Request("wait1"), run.Cancellation.Token).AsTask();
        run.RequestPause();
        Assert.Equal(0, Count(Snapshot(), ExecutionEventKinds.Paused));

        var second = run.WaitAsync(Request("wait2"), run.Cancellation.Token).AsTask();

        Assert.Equal(1, Count(Snapshot(), ExecutionEventKinds.Paused));
        Assert.Equal(1, slot.CurrentCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        run.Resume();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        AssertSettledExactlyOnce(Snapshot());
    }
}
