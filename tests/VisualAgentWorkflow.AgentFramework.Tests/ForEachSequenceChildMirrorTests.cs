using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070, "Slice (b) unblockers 1 and 2 (2026-11-26)", unblocker 1: the mirror of the tested composition in
/// SequenceForEachNestingEvidenceTests. A For Each body calls a pinned child workflow whose graph contains a Sequence. Every
/// guard is unchanged: the parent is a For Each graph and the child a Sequence graph; the child-run boundary keeps the
/// Sequence on its own runtime, owner and barrier, so the parent pass sees one call. This is evidence for a supported
/// composition today. It does not unblock slice (b) (a Sequence directly in a For Each body).
/// </summary>
public sealed class ForEachSequenceChildMirrorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private sealed class ItemAgent(Func<string, CancellationToken, Task<string>>? behavior = null) : IAgentNodeExecutor
    {
        public ConcurrentQueue<string> Inputs { get; } = new();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(input);
            return behavior is null ? Task.FromResult(input) : behavior(input, cancellationToken);
        }
    }

    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "data");

    internal static WorkflowDefinition SequenceChild(string workflowId) => new(WorkflowSchemaVersions.UnifiedWorkflowGraph,
        workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "Agent"), new("second", "Transform")]) },
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["upper"] = new(NodeTypes.SequenceUppercase, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-agent"] = Data("input", "out", "agent", "in"),
            ["input-upper"] = Data("input", "out", "upper", "in"),
            ["result"] = Data("agent", "out", "output", "in")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Execution("input", "then", "sequence", "execute"),
            ["sequence-first"] = Execution("sequence", "then.first", "agent", "execute"),
            ["first-output"] = Execution("agent", "out", "output", "execute"),
            ["sequence-second"] = Execution("sequence", "then.second", "upper", "execute"),
            ["second-output"] = Execution("upper", "then", "output", "execute")
        }.ToImmutableSortedDictionary()
    };

    internal static WorkflowDefinition ForEachParent(string workflowId, WorkflowRevision child) => new(
        WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]") },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["call"] = new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = Data("items", "out", "each", "array"),
            ["item-call"] = Data("each", "item", "call", "in"),
            ["result"] = Data("call", "out", "output", "in")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Execution("input", "then", "each", "execute"),
            ["body-start"] = Execution("each", "body", "body", "execute"),
            ["process"] = Execution("body", "then", "call", "execute"),
            ["return"] = Execution("call", "out", "each", "feedback"),
            ["complete"] = Execution("each", "completed", "output", "execute")
        }.ToImmutableSortedDictionary()
    };

    private sealed record Observed(WorkflowExecutionResult? Result, Exception? Failure, ExecutionEvent[] Events,
        WorkflowDispatchAdmissionOwner[] Owners, WorkflowRunner.StructuredActivationFrameOwner? RootFrameOwner);

    private static async Task<Observed> RunAsync(WorkflowRunner runner, WorkflowDefinition parent)
    {
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "mirror");
        var events = new ConcurrentQueue<ExecutionEvent>();
        var owners = new List<WorkflowDispatchAdmissionOwner>();
        WorkflowRunner.StructuredActivationFrameOwner? rootFrameOwner = null;
        WorkflowExecutionResult? result = null;
        Exception? failure = null;
        try
        {
            result = await runner.ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "start",
                _ => { }, evt => { events.Enqueue(evt); return ValueTask.CompletedTask; },
                ownerProbe: owner => { lock (owners) owners.Add(owner); },
                frameOwnerProbe: owner => rootFrameOwner ??= owner).WaitAsync(Window);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        lock (owners) return new(result, failure, events.ToArray(), [.. owners], rootFrameOwner);
    }

    private static string Trace(IEnumerable<ExecutionEvent> events) => string.Join(" | ",
        events.Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}:{evt.ScopeId}"));

    private static void AssertQuiescent(Observed run)
    {
        // One owner for the root run and one per child run (one call per pass).
        Assert.NotEmpty(run.Owners);
        Assert.All(run.Owners, owner => Assert.Empty(owner.Outstanding()));
        Assert.Equal(0, run.RootFrameOwner!.RunBudget.Reservation.DispatchTicketsOutstanding);
    }

    private static int IndexOf(ExecutionEvent[] events, Func<ExecutionEvent, bool> predicate)
    {
        var index = Array.FindIndex(events, evt => predicate(evt));
        Assert.True(index >= 0, "Expected event not found: " + Trace(events));
        return index;
    }

    /// <summary>The child Sequence lifecycle events grouped by Sequence invocation, in journal order.</summary>
    private static IReadOnlyList<(Guid Invocation, ExecutionEvent[] Events)> SequenceInvocations(IEnumerable<ExecutionEvent> events) =>
        events.Where(evt => SequenceEvidence.TryRead(evt, out _))
            .GroupBy(evt => { SequenceEvidence.TryRead(evt, out var evidence); return evidence!.SequenceInvocationId; })
            .Select(group => (group.Key, group.OrderBy(evt => evt.Sequence).ToArray()))
            .OrderBy(group => group.Item2[0].Sequence)
            .ToArray();

    [Fact]
    public async Task A_for_each_body_can_call_a_pinned_child_that_contains_a_sequence()
    {
        var agent = new ItemAgent();
        var child = WorkflowRevision.Create(SequenceChild("mirror-child"), "test");
        var parent = ForEachParent("mirror-parent", child);

        // Both graphs validate under the unchanged guards: the parent as a For Each graph (WorkflowCall is a supported body
        // node), the child as a unified Sequence graph.
        Assert.Empty(WorkflowValidator.Validate(parent));
        Assert.Empty(WorkflowValidator.Validate(child.Definition));
        var run = await RunAsync(new WorkflowRunner(agents: agent), parent);
        var events = run.Events;

        Assert.True(run.Failure is null, run.Failure + Environment.NewLine + Trace(events));
        Assert.False(run.Result!.Failed, Trace(events));
        // The last pass's child result: the child's Output reads its Agent, which echoes the item.
        Assert.Equal("c", run.Result.Output);
        Assert.Equal(["a", "b", "c"], agent.Inputs);
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);

        // Loop evidence for each pass, ending array-exhausted.
        Assert.Equal(["each/1", "each/2", "each/3"],
            events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).Select(evt => evt.ScopeId));
        Assert.Equal(["each/1", "each/2", "each/3"],
            events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).Select(evt => evt.ScopeId));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        Assert.Contains("array-exhausted", stopped.Data);

        // Each pass runs one child call and one child Sequence, and the child Sequence completes inside its pass: after
        // the pass starts and before the next pass (or the loop stop) begins.
        for (var pass = 1; pass <= 3; pass++)
        {
            var passStarted = IndexOf(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId == $"each/{pass}");
            var sequenceCompleted = IndexOf(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted && evt.ScopeId == $"each/{pass}/call");
            var subworkflowCompleted = IndexOf(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == $"each/{pass}/call");
            var next = pass < 3
                ? IndexOf(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId == $"each/{pass + 1}")
                : IndexOf(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
            Assert.True(passStarted < sequenceCompleted && sequenceCompleted < subworkflowCompleted && subworkflowCompleted < next,
                Trace(events));
        }

        // One child Sequence invocation per pass. Every lifecycle event of an invocation carries the one call scope of its
        // pass (each/N/call), and playback reconstructs when the journal is filtered to that invocation.
        var invocations = SequenceInvocations(events);
        Assert.Equal(3, invocations.Count);
        for (var pass = 1; pass <= 3; pass++)
        {
            var (_, lifecycle) = invocations[pass - 1];
            Assert.All(lifecycle, evt => Assert.Equal($"each/{pass}/call", evt.ScopeId));
            Assert.True(SequenceEvidence.TryReconstructPlayback(lifecycle, out var playback), Trace(lifecycle));
            Assert.Equal("completed", playback!.Status);
        }
        // The current reader pins one Sequence invocation and one scope, so the unfiltered run journal is refused.
        Assert.False(SequenceEvidence.TryReconstructPlayback(events, out _));

        // The passes come from the root run's reservation, and that reservation is the one the child runs inherit.
        var reservation = run.RootFrameOwner!.RunBudget.Reservation;
        Assert.Equal(3, reservation.BodyPassesUsed);
        Assert.True(reservation.EvidenceEventsUsed > 0);
        // Root owner plus one owner per child run.
        Assert.Equal(4, run.Owners.Length);
        AssertQuiescent(run);
    }

    [Fact]
    public async Task The_root_pass_pool_bounds_the_for_each_that_calls_a_sequence_child()
    {
        // Two passes allowed for the whole run: the third item is refused by the root pool after two child Sequences ran.
        var parent = ForEachParent("mirror-pool", WorkflowRevision.Create(SequenceChild("mirror-pool-child"), "test"));
        var events = new ConcurrentQueue<ExecutionEvent>();
        var agent = new ItemAgent();

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: agent)
            .ExecuteWithLoopBudgetsProofAsync(Guid.NewGuid(),
                WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "pool"), "start", 2, 1000,
                evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(Window));

        Assert.NotNull(failure);
        Assert.Contains("body-pass limit", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(["each/1", "each/2"],
            events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).Select(evt => evt.ScopeId));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceCompleted));
        Assert.Equal(["a", "b"], agent.Inputs);
        Assert.DoesNotContain(events, evt => evt.ScopeId?.StartsWith("each/3/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_child_sequence_output_failing_on_item_two_stops_the_for_each_before_item_three()
    {
        var agent = new ItemAgent((input, _) => input == "b"
            ? throw new InvalidOperationException("item failed") : Task.FromResult(input));
        var parent = ForEachParent("mirror-failing", WorkflowRevision.Create(SequenceChild("mirror-failing-child"), "test"));

        var run = await RunAsync(new WorkflowRunner(agents: agent), parent);
        var events = run.Events;

        Assert.True(run.Failure is not null || run.Result!.Failed, Trace(events));
        Assert.Equal(["a", "b"], agent.Inputs);
        // Item three never starts: no third pass, no child scope for it.
        Assert.Equal(["each/1", "each/2"],
            events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).Select(evt => evt.ScopeId));
        Assert.DoesNotContain(events, evt => evt.ScopeId?.StartsWith("each/3", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.LoopIterationCompleted && evt.ScopeId == "each/2");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        // The child Sequence output fails, the child Sequence fails, the call reports subworkflow.failed, then the root fails.
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed && evt.ScopeId == "each/2/call");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed && evt.ScopeId == "each/2/call");
        var subworkflowFailed = IndexOf(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "each/2/call");
        var rootFailed = IndexOf(events, evt => evt.NodeId is null && evt.ScopeId is null && evt.Kind == ExecutionEventKinds.Failed);
        Assert.True(subworkflowFailed < rootFailed, Trace(events));
        Assert.Single(events, evt => evt.NodeId is null && evt.ScopeId is null && evt.Kind == ExecutionEventKinds.Failed);
        Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.ScopeId is null && evt.Kind == ExecutionEventKinds.Completed);
        Assert.Equal(2, run.RootFrameOwner!.RunBudget.Reservation.BodyPassesUsed);
        AssertQuiescent(run);
    }

    private static IWorkflowRunStore Open(string directory, bool sqlite) => sqlite
        ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
        : new FileRunJournal(directory);

    /// <summary>Control child with no Sequence: Input to one Agent to Output.</summary>
    private static WorkflowDefinition AgentChild(string workflowId) => new(WorkflowSchemaVersions.UnifiedWorkflowGraph,
        workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["in"] = Data("input", "out", "agent", "in"),
            ["result"] = Data("agent", "out", "output", "in")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Execution("input", "then", "agent", "execute"),
            ["complete"] = Execution("agent", "out", "output", "execute")
        }.ToImmutableSortedDictionary()
    };

    private sealed record PassiveState(string Status, int EventCount, string[] Shape, string[] InvocationPlayback,
        bool WholeSequencePlayback, RunRecoveryDisposition Disposition, int UnresolvedWork, bool IncompleteCorrelation);

    /// <summary>Run-independent projection: no run, revision or invocation ids, only kinds, nodes, scopes and reader results.</summary>
    private static PassiveState Read(RunSnapshot snapshot)
    {
        var work = WorkflowWorkEvidence.Read(snapshot);
        var reconciliation = WorkflowRunReconciliation.Assess(snapshot, null);
        var playback = SequenceInvocations(snapshot.Events).Select(invocation =>
            SequenceEvidence.TryReconstructPlayback(invocation.Events, out var state)
                ? $"{invocation.Events[0].ScopeId}:{state!.Status}" : $"{invocation.Events[0].ScopeId}:refused").ToArray();
        return new(snapshot.Status, snapshot.Events.Length,
            snapshot.Events.Select(evt => $"{evt.Kind}|{evt.NodeId}|{evt.ScopeId}").ToArray(), playback,
            SequenceEvidence.TryReconstructPlayback(snapshot.Events, out _),
            reconciliation.Disposition, work.TotalUnresolvedWork, work.HasIncompleteCorrelation);
    }

    private static async Task<PassiveState> RunPersistAndReopenAsync(bool sqlite, WorkflowDefinition child)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-mirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(ForEachParent("mirror-persist", WorkflowRevision.Create(child, "test")), "test");
            var store = Open(directory, sqlite);
            var runId = store.Create(revision, "start").Header.RunId;
            var recorded = new List<ExecutionEvent>();
            var gate = new object();
            ValueTask Record(ExecutionEvent evt)
            {
                lock (gate)
                {
                    store.Append(runId, evt.Sequence - 1, [evt]);
                    recorded.Add(evt);
                }
                return ValueTask.CompletedTask;
            }
            var result = await new WorkflowRunner(agents: new ItemAgent())
                .ExecuteWithOutcomeAsync(runId, revision, "start", null, Record).WaitAsync(Window);
            Assert.False(result.Failed);

            var reopened = Open(directory, sqlite).Get(runId)!;
            // Event order and content, nested scope ids included, survive the reopen exactly.
            Assert.Equal(recorded, reopened.Events);
            Assert.Equal("completed", reopened.Status);
            return Read(reopened);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task The_for_each_sequence_child_journal_reopens_identically_from_the_file_journal_and_the_sqlite_store()
    {
        var file = await RunPersistAndReopenAsync(sqlite: false, SequenceChild("mirror-persist-child"));
        var sqlite = await RunPersistAndReopenAsync(sqlite: true, SequenceChild("mirror-persist-child"));

        // Each store returns exactly the history it recorded (asserted in RunPersistAndReopenAsync). Across the two runs the
        // same events, nodes and nested scopes occur; the two child Sequence outputs race, so the cross-run comparison is
        // order-insensitive. The readers' results are identical.
        Assert.Equal(file.Shape.Order(StringComparer.Ordinal), sqlite.Shape.Order(StringComparer.Ordinal));
        Assert.Equal(file.InvocationPlayback, sqlite.InvocationPlayback);
        Assert.Equal((file.Status, file.EventCount, file.WholeSequencePlayback, file.Disposition, file.UnresolvedWork, file.IncompleteCorrelation),
            (sqlite.Status, sqlite.EventCount, sqlite.WholeSequencePlayback, sqlite.Disposition, sqlite.UnresolvedWork, sqlite.IncompleteCorrelation));
        Assert.Equal(["each/1/call:completed", "each/2/call:completed", "each/3/call:completed"], file.InvocationPlayback);
        Assert.False(file.WholeSequencePlayback);
        Assert.Equal(0, file.UnresolvedWork);
        // Finding (Decision 0070, unblocker 1): the work reader projects child-run node events against the PARENT
        // definition, so child node ids it does not contain mark the correlation incomplete and the completed run reads as
        // UnresolvedWork. The control below shows the same result without a Sequence, so this is a WorkflowCall-child
        // reader limit, not a Sequence one. Desired behavior is pinned by the skipped test below.
        Assert.True(file.IncompleteCorrelation);
        Assert.Equal(RunRecoveryDisposition.UnresolvedWork, file.Disposition);

        var control = await RunPersistAndReopenAsync(sqlite: false, AgentChild("mirror-persist-control-child"));
        Assert.Equal((0, true, RunRecoveryDisposition.UnresolvedWork),
            (control.UnresolvedWork, control.IncompleteCorrelation, control.Disposition));
    }

    [Fact(Skip = "Finding (Decision 0070, slice (b) unblocker 1): WorkflowWorkEvidence reads a For Each run that calls a child " +
        "workflow as incompletely correlated, because child node events are checked against the parent definition. The same " +
        "happens with a child that has no Sequence. Unskip once the work reader scopes child-run node events by call scope.")]
    public async Task A_completed_for_each_sequence_child_run_reconciles_as_terminal_after_reopen()
    {
        foreach (var sqlite in new[] { false, true })
        {
            var state = await RunPersistAndReopenAsync(sqlite, SequenceChild("mirror-terminal-child"));
            Assert.False(state.IncompleteCorrelation);
            Assert.Equal(RunRecoveryDisposition.Terminal, state.Disposition);
        }
    }
}
