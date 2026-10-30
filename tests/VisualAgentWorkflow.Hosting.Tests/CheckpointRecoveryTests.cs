using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class CheckpointRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-recovery-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");
    private SqliteWorkflowRunStore Store() => new(Database);
    private static WorkflowRevision Revision() => WorkflowRevision.Create(WorkflowTemplates.Create("uppercase-reverse"), "test");

    [Fact]
    public async Task SavedNativeStateResumesAfterHostReplacementWithoutDuplicateNodes()
    {
        var store = Store();
        RunSnapshot suspended;
        await using (var first = new CheckpointWorkflowHost(store))
            suspended = await first.StartAsync(Revision(), "abc 🌍", 2);
        Assert.Equal("suspended", suspended.Status);
        var checkpoint = store.GetCheckpoint(suspended.Header.RunId)!;
        Assert.Equal(suspended.Events.Length, checkpoint.Sequence);
        Assert.True(WorkflowCheckpointRecovery.Assess(suspended, checkpoint).CanResume);
        Assert.Equal("suspended", (await JournalRunObserver.FromStore(store).GetRunAsync(suspended.Header.RunId))!.Status);
        await using var second = new CheckpointWorkflowHost(Store());
        var completed = await second.ResumeAsync(suspended.Header.RunId, checkpoint.Native.CheckpointId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("🌍 CBA", completed.Events[^1].Data);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(suspended.Header), System.Text.Json.JsonSerializer.Serialize(completed.Header));
        Assert.Equal(new[] { "input", "step-1", "step-2", "output" }, completed.Events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.NodeId));
        Assert.Equal(Enumerable.Range(1, completed.Events.Length).Select(i => (long)i), completed.Events.Select(e => e.Sequence));
        Assert.False(WorkflowCheckpointRecovery.Assess(completed, store.GetCheckpoint(completed.Header.RunId)).CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.ResumeAsync(completed.Header.RunId, store.GetCheckpoint(completed.Header.RunId)!.Native.CheckpointId));
    }

    [Fact]
    public async Task OwnersExcludeEachOtherWithoutInterruptingSavedOrPausedRuns()
    {
        var store = Store();
        await using (var first = new CheckpointWorkflowHost(store))
        {
            var run = await first.StartAsync(Revision(), "abc", 1);
            Assert.Throws<InvalidOperationException>(() => new CheckpointWorkflowHost(Store()));
            using var competing = new LocalWorkflowRuntime(Store(), NullLogger<LocalWorkflowRuntime>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => competing.StartAsync(CancellationToken.None));
            await competing.StopAsync(CancellationToken.None);
            Assert.Equal("suspended", store.Get(run.Header.RunId)!.Status);
        }
        await using var next = new CheckpointWorkflowHost(Store());
    }

    [Fact]
    public async Task Concurrent_disposal_is_idempotent_and_releases_store_ownership()
    {
        var host = new CheckpointWorkflowHost(Store());

        await Task.WhenAll(host.DisposeAsync().AsTask(), host.DisposeAsync().AsTask());

        await using var replacement = new CheckpointWorkflowHost(Store());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync(Revision(), "abc", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalRuntimeOwnershipIsReleasedOnlyByItsOwner(bool sqlite)
    {
        IWorkflowRunStore StoreForRuntime() => sqlite ? Store() : new FileRunJournal(directory);
        using var first = new LocalWorkflowRuntime(StoreForRuntime(), NullLogger<LocalWorkflowRuntime>.Instance);
        using var second = new LocalWorkflowRuntime(StoreForRuntime(), NullLogger<LocalWorkflowRuntime>.Instance);
        await first.StartAsync(CancellationToken.None);
        var run = await first.StartPausedRunAsync(WorkflowTemplates.Create("uppercase"), "abc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(CancellationToken.None));
        await second.StopAsync(CancellationToken.None);
        Assert.Equal("paused", (await first.GetRunAsync(run.Header.RunId))!.Status);
        await first.StopAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        // Repeated cleanup of the previous owner must not interrupt the new owner's run.
        var next = await second.StartPausedRunAsync(WorkflowTemplates.Create("uppercase"), "next");
        await first.StopAsync(CancellationToken.None);
        Assert.Equal("paused", (await second.GetRunAsync(next.Header.RunId))!.Status);
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleEvidenceAndCheckpointIdentityCannotBeReplayed()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var run = await runtime.StartAsync(Revision(), "abc", 1);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;
        await Assert.ThrowsAsync<WorkflowRunStoreConflictException>(() => runtime.ResumeAsync(run.Header.RunId, "stale"));
        Assert.Equal(run.Events.Length, store.Get(run.Header.RunId)!.Events.Length);
        store.Append(new(run.Header.RunId, run.Header.RevisionId, run.Events.Length + 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Recovered));
        Assert.False(WorkflowCheckpointRecovery.Assess(store.Get(run.Header.RunId)!, checkpoint).CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResumeAsync(run.Header.RunId, checkpoint.Native.CheckpointId));
        Assert.Equal(run.Events.Length + 1, store.Get(run.Header.RunId)!.Events.Length);
    }

    [Fact]
    public async Task Unsettled_recorded_work_cannot_reuse_a_saved_checkpoint_boundary()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var run = await runtime.StartAsync(Revision(), "abc", 1);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;
        var invocation = Guid.NewGuid();
        var unsettledEvent = new ExecutionEvent(run.Header.RunId, run.Header.RevisionId,
            run.Events.Length + 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocation);
        var secondSuspension = new ExecutionEvent(run.Header.RunId, run.Header.RevisionId,
            run.Events.Length + 2, DateTimeOffset.UtcNow, ExecutionEventKinds.Suspended);
        var unsettled = run with { Events = run.Events.Add(unsettledEvent).Add(secondSuspension) };
        var alignedCheckpoint = checkpoint with { Sequence = unsettled.Events.Length };

        var assessment = WorkflowCheckpointRecovery.Assess(unsettled, alignedCheckpoint);

        Assert.False(assessment.CanResume);
        Assert.Equal("Recorded work is unsettled or ambiguously correlated.", assessment.Reason);
    }

    [Fact]
    public async Task ExternalNodesFailBeforeAdmissionAndCorruptOrForeignStateCannotResume()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var external = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new("absent", null, "instructions", [])));
        var mcp = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.McpTool, "1.0.0", new("fixture", new string('0', 64))));
        foreach (var definition in new[] { external, mcp, GraphEditing.InsertApprovalBefore(mcp, "step-1") })
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(WorkflowRevision.Create(definition, "test"), "abc", 1));
        Assert.Empty(store.List());
        var run = await runtime.StartAsync(Revision(), "abc", 1);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;
        foreach (var bad in new[] { checkpoint with { RuntimeVersion = "future" }, checkpoint with { RevisionId = Guid.NewGuid() },
            checkpoint with { PayloadHash = "corrupt" }, checkpoint with { SchemaVersion = "future" } })
            Assert.False(WorkflowCheckpointRecovery.Assess(run, bad).CanResume);
        Assert.Equal(run.Events.Length, store.Get(run.Header.RunId)!.Events.Length);
    }

    [Fact]
    public async Task Committed_suspension_adoption_requires_exact_native_identity()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var run = await runtime.StartAsync(Revision(), "abc", 1);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;

        Assert.True(WorkflowCheckpointRecovery.IsCommittedSuspension(run, checkpoint, checkpoint.Native));
        Assert.False(WorkflowCheckpointRecovery.IsCommittedSuspension(run, checkpoint,
            checkpoint.Native with { CheckpointId = "different" }));
        Assert.False(WorkflowCheckpointRecovery.IsCommittedSuspension(run, checkpoint,
            checkpoint.Native with { Payload = "{}" }));
    }

    [Fact]
    public async Task Host_reconciles_a_checkpoint_when_commit_acknowledgement_is_lost()
    {
        var inner = Store();
        await using var runtime = new CheckpointWorkflowHost(new AcknowledgementLossStore(inner));

        var run = await runtime.StartAsync(Revision(), "abc", 1);

        Assert.Equal("suspended", run.Status);
        var checkpoint = inner.GetCheckpoint(run.Header.RunId)!;
        Assert.True(WorkflowCheckpointRecovery.IsCommittedSuspension(run, checkpoint, checkpoint.Native));
    }

    [Fact]
    public async Task Host_publication_failure_records_failure_without_partial_checkpoint_state()
    {
        var inner = Store();
        await using var runtime = new CheckpointWorkflowHost(new PublicationFailureStore(inner));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(Revision(), "abc", 1));

        var run = Assert.Single(inner.List());
        Assert.Equal("failed", run.Status);
        Assert.Null(inner.GetCheckpoint(run.Header.RunId));
        Assert.DoesNotContain(run.Events, evt => evt.Kind is ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended);
    }

    [Fact]
    public async Task Host_cancellation_before_checkpoint_publication_records_cancelled_without_partial_state()
    {
        var inner = Store();
        using var cancellation = new CancellationTokenSource();
        await using var runtime = new CheckpointWorkflowHost(new CancelBeforePublicationStore(inner, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.StartAsync(Revision(), "abc", 1, cancellation.Token));

        var run = Assert.Single(inner.List());
        Assert.Equal("cancelled", run.Status);
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
        Assert.Null(inner.GetCheckpoint(run.Header.RunId));
        Assert.DoesNotContain(run.Events, evt => evt.Kind is ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended);
    }

    [Fact]
    public async Task Host_cancellation_during_checkpoint_publication_preserves_the_committed_suspension()
    {
        var inner = Store();
        using var cancellation = new CancellationTokenSource();
        await using var runtime = new CheckpointWorkflowHost(new CancelDuringPublicationStore(inner, cancellation));

        var run = await runtime.StartAsync(Revision(), "abc", 1, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("suspended", run.Status);
        var checkpoint = inner.GetCheckpoint(run.Header.RunId)!;
        Assert.True(WorkflowCheckpointRecovery.Assess(run, checkpoint).CanResume);
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.Suspended);
    }

    [Fact]
    public async Task Host_disposal_waits_for_active_checkpoint_publication_and_releases_ownership()
    {
        var inner = Store();
        var publication = new BlockingPublicationStore(inner);
        var runtime = new CheckpointWorkflowHost(publication);

        try
        {
            var runTask = runtime.StartAsync(Revision(), "abc", 1);
            await publication.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposeTask = runtime.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.False(disposeTask.IsCompleted);

            publication.Release();
            var run = await runTask;
            await disposeTask;

            Assert.Equal("suspended", run.Status);
            Assert.True(WorkflowCheckpointRecovery.Assess(run, inner.GetCheckpoint(run.Header.RunId)).CanResume);
            await using var replacement = new CheckpointWorkflowHost(inner);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public void Malformed_persisted_run_state_is_reported_as_ineligible_instead_of_throwing()
    {
        var malformed = new RunSnapshot(null!, []);
        var checkpoint = new WorkflowRunCheckpoint(
            "1.0", WorkflowRunner.CheckpointRuntimeVersion, Guid.NewGuid(), Guid.NewGuid(),
            "invalid", 1, new NativeWorkflowCheckpoint(Guid.NewGuid().ToString("D"), "checkpoint", null, "{}"),
            WorkflowRunCheckpoint.Hash("{}"));

        var assessment = WorkflowCheckpointRecovery.Assess(malformed, checkpoint);

        Assert.False(assessment.CanResume);
        Assert.Equal("The checkpoint is incompatible or invalid.", assessment.Reason);
    }

    [Theory]
    [InlineData(WorkflowSchemaVersions.MutableVariablesGraph)]
    [InlineData(WorkflowSchemaVersions.UnifiedWorkflowGraph)]
    public async Task VariableGraphsRestore_state_before_resuming_execution(string schemaVersion)
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);

        var suspended = await runtime.StartAsync(VariableRevision(schemaVersion), "next", 2);

        Assert.Equal("suspended", suspended.Status);
        var checkpoint = store.GetCheckpoint(suspended.Header.RunId)!;
        Assert.NotNull(checkpoint.Native.VariableState);
        Assert.Equal(schemaVersion, suspended.Header.Definition.SchemaVersion);
        Assert.Null(checkpoint.Frame);
        Assert.Equal(checkpoint.Native.VariableState!.ComputeHash(), checkpoint.VariableStateHash);
        Assert.True(WorkflowCheckpointRecovery.Assess(suspended, checkpoint).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(suspended, checkpoint with { VariableStateHash = "corrupt" }).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(suspended,
            checkpoint with { Native = checkpoint.Native with { VariableState = null }, VariableStateHash = null }).CanResume);

        var completed = await runtime.ResumeAsync(suspended.Header.RunId, checkpoint.Native.CheckpointId);

        Assert.Equal("completed", completed.Status);
        Assert.Equal("seed", completed.Events[^1].Data);
        var writes = completed.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal([2L, 3L], writes.Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task CheckpointPublicationFailureRollsBackBothPointerAndAudit()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var run = await runtime.StartAsync(Revision(), "abc", 1);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_checkpoint BEFORE INSERT ON run_checkpoints BEGIN SELECT RAISE(ABORT,'injected'); END;";
        command.ExecuteNonQuery();
        var nextEvent = new ExecutionEvent(run.Header.RunId, run.Header.RevisionId, run.Events.Length + 1, DateTimeOffset.UtcNow, ExecutionEventKinds.CheckpointCaptured);
        Assert.Throws<SqliteException>(() => store.CommitCheckpoint(run.Header.RunId, run.Events.Length, [nextEvent], checkpoint with { Sequence = nextEvent.Sequence }));
        Assert.Equal(run.Events.ToArray(), store.Get(run.Header.RunId)!.Events.ToArray());
        Assert.Equal(checkpoint, store.GetCheckpoint(run.Header.RunId));
    }

    [Theory]
    [InlineData("yes", "SEY")]
    [InlineData("no", "on")]
    public async Task ConditionalRecoveryPreservesChosenPathAndCanSuspendAgain(string input, string expected)
    {
        var definition = GraphEditing.InsertConditionalAfter(WorkflowTemplates.Create("reverse"), "input", "out");
        var branch = definition.Nodes.Single(node => node.Value.Type == NodeTypes.Condition).Key;
        definition = GraphEditing.InsertNodeAfter(definition, branch, "true", new(NodeTypes.Uppercase, "1.0.0"));
        var revision = WorkflowRevision.Create(definition, "test");
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var first = await runtime.StartAsync(revision, input, 2); // input and branch
        var second = await runtime.ResumeAsync(first.Header.RunId, store.GetCheckpoint(first.Header.RunId)!.Native.CheckpointId, 3);
        Assert.Equal("suspended", second.Status);
        var result = await runtime.ResumeAsync(first.Header.RunId, store.GetCheckpoint(first.Header.RunId)!.Native.CheckpointId);
        Assert.Equal("completed", result.Status);
        Assert.Equal(expected, result.Events[^1].Data);
        Assert.Single(result.Events, e => e.Kind == ExecutionEventKinds.BranchSelected);
        var invocations = result.Events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).ToArray();
        Assert.Equal(invocations.Length, invocations.Select(e => e.NodeId).Distinct().Count());
        Assert.Equal(2, result.Events.Count(e => e.Kind == ExecutionEventKinds.Recovered));
    }

    [Fact]
    public void VersionOneDatabaseMigratesWithoutRewritingRunEvidence()
    {
        var store = Store();
        var run = store.Create(Revision(), "preserve");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE run_checkpoints; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        var migrated = Store();
        Assert.Null(migrated.GetCheckpoint(run.Header.RunId));
        Assert.Empty(migrated.Get(run.Header.RunId)!.Events);
        Assert.Equal(run.Header.ContentHash, migrated.Get(run.Header.RunId)!.Header.ContentHash);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class AcknowledgementLossStore(SqliteWorkflowRunStore inner) : IWorkflowCheckpointHostStore
    {
        private bool loseAcknowledgement = true;

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);

        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint)
        {
            var result = inner.CommitCheckpoint(runId, expectedSequence, events, checkpoint);
            if (loseAcknowledgement)
            {
                loseAcknowledgement = false;
                throw new IOException("Injected lost checkpoint acknowledgement.");
            }
            return result;
        }
    }

    private sealed class PublicationFailureStore(SqliteWorkflowRunStore inner) : IWorkflowCheckpointHostStore
    {
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);
        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint) => throw new IOException("Injected checkpoint publication failure.");
    }

    private sealed class CancelBeforePublicationStore(SqliteWorkflowRunStore inner, CancellationTokenSource cancellation) : IWorkflowCheckpointHostStore
    {
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);
        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class CancelDuringPublicationStore(SqliteWorkflowRunStore inner, CancellationTokenSource cancellation) : IWorkflowCheckpointHostStore
    {
        private int cancelled;

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);
        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint)
        {
            if (Interlocked.Exchange(ref cancelled, 1) == 0) cancellation.Cancel();
            return inner.CommitCheckpoint(runId, expectedSequence, events, checkpoint);
        }
    }

    private sealed class BlockingPublicationStore(SqliteWorkflowRunStore inner) : IWorkflowCheckpointHostStore
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);
        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint)
        {
            Entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return inner.CommitCheckpoint(runId, expectedSequence, events, checkpoint);
        }

        public void Release() => release.TrySetResult(true);
    }

    private static WorkflowRevision VariableRevision(string schemaVersion = WorkflowSchemaVersions.MutableVariablesGraph)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("seed") },
            ["constant-again"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("seed") },
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["set-again"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["constant-value"] = new(new("constant", "out"), new("set", "value"), "data"),
            ["constant-value-again"] = new(new("constant-again", "out"), new("set-again", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-set"] = new(new("input", "then"), new("set", "execute"), "execution"),
            ["set-again"] = new(new("set", "then"), new("set-again", "execute"), "execution"),
            ["set-get"] = new(new("set-again", "then"), new("get", "execute"), "execution"),
            ["get-output"] = new(new("get", "then"), new("output", "execute"), "execution")
        };
        var definition = new WorkflowDefinition(schemaVersion, "checkpoint-variable", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
        return WorkflowRevision.Create(definition, "checkpoint-variable");
    }
}
