using System.Collections.Concurrent;
using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070, "Prerequisite proofs: forEachItems isolation and nested-scope playback (2026-11-25)". A For Each nested in a
/// For Each persists iterator scope ids such as <c>outer/1/inner/2</c>. These tests run it through the File journal and the
/// SQLite store, reopen, and read the stored evidence with the current passive readers.
/// </summary>
public sealed class NestedIteratorScopePlaybackTests
{
    private sealed class EchoAgent : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default) => Task.FromResult(input);
    }

    /// <summary>Outer For Each over ["x","y"]; each pass runs an inner For Each over ["1","2"] (same graph as ForEachExecutionTests).</summary>
    internal static WorkflowDefinition NestedWorkflow()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["outer-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\",\"y\"]") },
            ["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"1\",\"2\"]") },
            ["outer"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["inner"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["outer-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["inner-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["text"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["outer-array"] = new(new("outer-items", "out"), new("outer", "array"), "data"),
            ["inner-array"] = new(new("inner-items", "out"), new("inner", "array"), "data"),
            ["outer-item"] = new(new("outer", "item"), new("text", "left"), "data"),
            ["inner-item"] = new(new("inner", "item"), new("text", "right"), "data"),
            ["text-agent"] = new(new("text", "out"), new("agent", "in"), "data"),
            ["result"] = new(new("done-text", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-nested", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("outer", "execute"), "execution"),
                ["outer-start"] = new(new("outer", "body"), new("outer-body", "execute"), "execution"),
                ["enter-inner"] = new(new("outer-body", "then"), new("inner", "execute"), "execution"),
                ["inner-start"] = new(new("inner", "body"), new("inner-body", "execute"), "execution"),
                ["process"] = new(new("inner-body", "then"), new("agent", "execute"), "execution"),
                ["inner-return"] = new(new("agent", "out"), new("inner", "feedback"), "execution"),
                ["outer-return"] = new(new("inner", "completed"), new("outer", "feedback"), "execution"),
                ["complete"] = new(new("outer", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    private static IWorkflowRunStore Open(string directory, bool sqlite) => sqlite
        ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
        : new FileRunJournal(directory);

    private static string[] LoopEvidence(IEnumerable<ExecutionEvent> events) => events
        .Where(evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or
            ExecutionEventKinds.LoopStopped)
        .Select(evt => $"{evt.Kind}|{evt.NodeId}|{evt.ScopeId}").ToArray();

    // The wire format of nested iterator scopes: the outer pass scope (outer/N), then the inner pass scope under it
    // (outer/N/inner/M). A LoopStopped event carries the scope of the pass that was last open.
    private static readonly string[] ExpectedLoopEvidence =
    [
        "loop.iteration.started|outer|outer/1",
        "loop.iteration.started|inner|outer/1/inner/1",
        "loop.iteration.completed|inner|outer/1/inner/1",
        "loop.iteration.started|inner|outer/1/inner/2",
        "loop.iteration.completed|inner|outer/1/inner/2",
        "loop.stopped|inner|outer/1/inner/2",
        "loop.iteration.completed|outer|outer/1",
        "loop.iteration.started|outer|outer/2",
        "loop.iteration.started|inner|outer/2/inner/1",
        "loop.iteration.completed|inner|outer/2/inner/1",
        "loop.iteration.started|inner|outer/2/inner/2",
        "loop.iteration.completed|inner|outer/2/inner/2",
        "loop.stopped|inner|outer/2/inner/2",
        "loop.iteration.completed|outer|outer/2",
        "loop.stopped|outer|outer/2"
    ];

    private static async Task<(Guid RunId, ExecutionEvent[] Recorded)> RunAndPersistAsync(string directory, bool sqlite)
    {
        var revision = WorkflowRevision.Create(NestedWorkflow(), "nested-scope-playback");
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
        var result = await new WorkflowRunner(agents: new EchoAgent())
            .ExecuteWithOutcomeAsync(runId, revision, "start", null, Record).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Failed);
        return (runId, recorded.ToArray());
    }

    private sealed record PassiveState(string Status, int EventCount, string[] LoopEvidence, bool SequencePlayback,
        bool CallPlayback, string? JoinStatus, int JoinActivations, RunRecoveryDisposition Disposition,
        int UnresolvedWork, bool IncompleteCorrelation);

    private static PassiveState Read(RunSnapshot snapshot)
    {
        var joinOk = ParallelJoinEvidence.TryReconstructPlayback(snapshot.Header.Definition, snapshot.Events, out var join);
        var work = WorkflowWorkEvidence.Read(snapshot);
        var reconciliation = WorkflowRunReconciliation.Assess(snapshot, null);
        return new(snapshot.Status, snapshot.Events.Length, LoopEvidence(snapshot.Events),
            SequenceEvidence.TryReconstructPlayback(snapshot.Events, out _),
            WorkflowCallEvidence.TryReconstructPlayback(snapshot.Events, out _),
            joinOk ? join!.Status : null, joinOk ? join!.Activations.Length : -1,
            reconciliation.Disposition, work.TotalUnresolvedWork, work.HasIncompleteCorrelation);
    }

    [Fact]
    public async Task Nested_iterator_scope_ids_reopen_identically_from_the_file_journal_and_the_sqlite_store()
    {
        var states = new List<PassiveState>();
        foreach (var sqlite in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), $"vaw-nested-scope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var (runId, recorded) = await RunAndPersistAsync(directory, sqlite);

                var reopened = Open(directory, sqlite).Get(runId)!;
                // Event order and content survive the reopen exactly, scope ids included.
                Assert.Equal(recorded, reopened.Events);
                Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
                    reopened.Events.Select(evt => evt.Sequence));
                Assert.Equal("completed", reopened.Status);
                Assert.Equal(ExpectedLoopEvidence, LoopEvidence(reopened.Events));
                var state = Read(reopened);
                // The same reader over the in-memory events and the in-memory snapshot of the live run agrees.
                Assert.Equal(ExpectedLoopEvidence, state.LoopEvidence);
                states.Add(state);
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }

        // File and SQLite reconstruct the same passive state: status, counts, scope evidence and readers.
        Assert.Equal(states[0].Status, states[1].Status);
        Assert.Equal(states[0].EventCount, states[1].EventCount);
        Assert.Equal(states[0].LoopEvidence, states[1].LoopEvidence);
        Assert.Equal((states[0].SequencePlayback, states[0].CallPlayback, states[0].JoinStatus, states[0].JoinActivations,
                states[0].Disposition, states[0].UnresolvedWork, states[0].IncompleteCorrelation),
            (states[1].SequencePlayback, states[1].CallPlayback, states[1].JoinStatus, states[1].JoinActivations,
                states[1].Disposition, states[1].UnresolvedWork, states[1].IncompleteCorrelation));
        // None of the Sequence, call or Join readers has lifecycle evidence to reconstruct in a For Each only graph (each
        // returns false because there is nothing to project, and the Join reader returns false for zero activations),
        // and the nested scope ids make none of them throw. Reconciliation reads the same journal as terminal and settled.
        Assert.False(states[0].SequencePlayback);
        Assert.False(states[0].CallPlayback);
        Assert.Equal((null, -1), (states[0].JoinStatus, states[0].JoinActivations));
        Assert.Equal(RunRecoveryDisposition.Terminal, states[0].Disposition);
        Assert.Equal(0, states[0].UnresolvedWork);
        Assert.False(states[0].IncompleteCorrelation);
    }

    /// <summary>
    /// A frozen journal in the current wire format, written as literal events (not produced by a run) so the scope ids
    /// stay pinned even if the runner changes: older-reader and newer-reader compatibility is judged against this text.
    /// </summary>
    private static ExecutionEvent[] FrozenNestedJournal(WorkflowRevision revision, Guid runId)
    {
        var at = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        long sequence = 0;
        ExecutionEvent E(string kind, string? node = null, string? scope = null, string? data = null) =>
            new(runId, revision.RevisionId, ++sequence, at, kind, node, Data: data) { ScopeId = scope };
        return
        [
            E(ExecutionEventKinds.Started),
            E(ExecutionEventKinds.LoopIterationStarted, "outer", "outer/1", "{\"iteration\":1,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.LoopIterationStarted, "inner", "outer/1/inner/1", "{\"iteration\":1,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.LoopIterationCompleted, "inner", "outer/1/inner/1", "{\"iteration\":1}"),
            E(ExecutionEventKinds.LoopIterationStarted, "inner", "outer/1/inner/2", "{\"iteration\":2,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.LoopIterationCompleted, "inner", "outer/1/inner/2", "{\"iteration\":2}"),
            E(ExecutionEventKinds.LoopStopped, "inner", "outer/1/inner/2", "{\"reason\":\"array-exhausted\",\"iterations\":2,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.LoopIterationCompleted, "outer", "outer/1", "{\"iteration\":1}"),
            E(ExecutionEventKinds.LoopIterationStarted, "outer", "outer/2", "{\"iteration\":2,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.LoopIterationCompleted, "outer", "outer/2", "{\"iteration\":2}"),
            E(ExecutionEventKinds.LoopStopped, "outer", "outer/2", "{\"reason\":\"array-exhausted\",\"iterations\":2,\"maximumIterations\":2}"),
            E(ExecutionEventKinds.Completed, data: "done")
        ];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_frozen_journal_with_nested_iterator_scope_ids_is_read_by_the_current_playback_and_reconciliation_paths(bool sqlite)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-nested-frozen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(NestedWorkflow(), "nested-frozen");
            var store = Open(directory, sqlite);
            var runId = store.Create(revision, "start").Header.RunId;
            var frozen = FrozenNestedJournal(revision, runId);
            store.Append(runId, 0, frozen);

            var reopened = Open(directory, sqlite).Get(runId)!;
            Assert.Equal(frozen, reopened.Events);
            Assert.Equal("completed", reopened.Status);
            Assert.Equal(frozen.Where(evt => evt.ScopeId is not null).Select(evt => evt.ScopeId),
                reopened.Events.Where(evt => evt.ScopeId is not null).Select(evt => evt.ScopeId));
            var state = Read(reopened);
            Assert.False(state.SequencePlayback);
            Assert.False(state.CallPlayback);
            Assert.Equal(RunRecoveryDisposition.Terminal, state.Disposition);
            Assert.Equal(0, state.UnresolvedWork);
            Assert.False(state.IncompleteCorrelation);
            // The checkpoint frame validators only know top-level For Each and Loop cursors; a nested scope id journal is
            // never fed to them because the nested graph is refused for checkpoint recovery up front.
            Assert.ThrowsAny<InvalidOperationException>(() =>
                WorkflowRunner.ValidateCheckpointGraph(NestedWorkflow(), allowLoopCheckpointRecovery: true));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
