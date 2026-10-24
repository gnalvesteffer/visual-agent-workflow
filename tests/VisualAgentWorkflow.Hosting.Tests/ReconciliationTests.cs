using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ReconciliationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-reconciliation-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");

    [Fact]
    public async Task SavedBoundaryAndAdvancedEvidenceHaveDistinctReadOnlyAssessments()
    {
        var store = new SqliteWorkflowRunStore(Database);
        await using var host = new CheckpointWorkflowHost(store);
        var run = await host.StartAsync(WorkflowRevision.Create(PipelineTemplates.Create("reverse"), "test"), "private payload", 1);
        var observer = JournalRunObserver.FromStore(store);
        var saved = (await observer.GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal(RunRecoveryDisposition.SavedSuspension, saved.Disposition);
        Assert.True(saved.CanResume);
        Assert.Equal(run.Events.Length, saved.LastSequence);
        var serialized = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain("private payload", serialized);
        Assert.DoesNotContain("Native", serialized);
        Assert.Equal(run.Events.ToArray(), store.Get(run.Header.RunId)!.Events.ToArray());
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;
        store.Append(new(run.Header.RunId, run.Header.RevisionId, run.Events.Length + 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Recovered));
        var advanced = (await observer.GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal(RunRecoveryDisposition.EvidenceAdvanced, advanced.Disposition);
        Assert.False(advanced.CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResumeAsync(run.Header.RunId, checkpoint.Native.CheckpointId));
    }

    [Fact]
    public async Task InvalidNativeStateCanBeExplainedWithoutExecutingOrExposingIt()
    {
        var store = new SqliteWorkflowRunStore(Database);
        await using var host = new CheckpointWorkflowHost(store);
        var run = await host.StartAsync(WorkflowRevision.Create(PipelineTemplates.Create("reverse"), "test"), "private", 1);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE run_checkpoints SET checkpoint_json='private invalid state';";
        command.ExecuteNonQuery();
        var report = (await JournalRunObserver.FromStore(store).GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal(RunRecoveryDisposition.CheckpointIncompatible, report.Disposition);
        Assert.False(report.CanResume);
        Assert.Null(report.Checkpoint);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
        Assert.Equal(run.Events.Length, store.Get(run.Header.RunId)!.Events.Length);
    }

    [Fact]
    public async Task FileStoreExplainsUnsettledTerminalWorkWithoutProviderOrToolServices()
    {
        var store = new FileRunJournal(directory);
        var run = store.Create(WorkflowRevision.Create(PipelineTemplates.Create("reverse"), "test"), "private");
        var invocation = Guid.NewGuid();
        var call = Guid.NewGuid();
        var data = JsonSerializer.Serialize(new { modelCallId = call, instructions = "private" });
        store.Append(run.Header.RunId, 0,
            [new(run.Header.RunId, run.Header.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocation, Data: data),
             new(run.Header.RunId, run.Header.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.Failed)]);
        var report = (await JournalRunObserver.FromStore(store).GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal("failed", report.Status);
        Assert.Equal(RunRecoveryDisposition.UnresolvedWork, report.Disposition);
        Assert.False(report.CanResume);
        Assert.Equal(call, Assert.Single(report.UnresolvedWork).CallId);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
        Assert.Null(await JournalRunObserver.FromStore(store).GetReconciliationAsync(Guid.NewGuid()));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
