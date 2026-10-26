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
    public async Task ExternalNodesFailBeforeAdmissionAndCorruptOrForeignStateCannotResume()
    {
        var store = Store();
        await using var runtime = new CheckpointWorkflowHost(store);
        var external = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.Agent, "1.0.0", Agent: new("absent", "instructions", [])));
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
}
