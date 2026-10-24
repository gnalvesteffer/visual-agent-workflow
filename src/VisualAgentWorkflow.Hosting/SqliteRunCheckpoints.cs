using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public sealed partial class SqliteWorkflowRunStore
{
    public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events, WorkflowRunCheckpoint checkpoint)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = Read(connection, transaction, runId) ?? throw new KeyNotFoundException("Unknown run.");
        var next = RunStoreRecords.Apply(snapshot, expectedSequence, events);
        checkpoint.Validate(next);
        if (checkpoint.Sequence != next.Events.Length || !next.Events.Skip(snapshot.Events.Length).Any(e => e.Kind == ExecutionEventKinds.CheckpointCaptured))
            throw new InvalidOperationException("A checkpoint must bind this commit's capture evidence and final sequence.");
        WriteTransition(connection, transaction, snapshot, next);
        Execute(connection, transaction, """
            INSERT INTO run_checkpoints(run_id,checkpoint_json) VALUES($id,$checkpoint)
            ON CONFLICT(run_id) DO UPDATE SET checkpoint_json=$checkpoint;
            """, ("$id", runId.ToString()), ("$checkpoint", JsonSerializer.Serialize(checkpoint, Json)));
        transaction.Commit();
        return next;
    }

    public WorkflowRunCheckpoint? GetCheckpoint(Guid runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = Read(connection, transaction, runId);
        if (snapshot is null) return null;
        var checkpoint = ReadCheckpoint(connection, transaction, runId);
        checkpoint?.Validate(snapshot);
        transaction.Commit();
        return checkpoint;
    }

    public RunReconciliation? GetReconciliation(Guid runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = Read(connection, transaction, runId);
        if (snapshot is null) return null;
        RunReconciliation report;
        try { report = WorkflowRunReconciliation.Assess(snapshot, ReadCheckpoint(connection, transaction, runId)); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { report = WorkflowRunReconciliation.Assess(snapshot, null, checkpointInvalid: true); }
        transaction.Commit();
        return report;
    }

    private static WorkflowRunCheckpoint? ReadCheckpoint(SqliteConnection connection, SqliteTransaction transaction, Guid runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT checkpoint_json FROM run_checkpoints WHERE run_id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString());
        var json = command.ExecuteScalar() as string;
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<WorkflowRunCheckpoint>(json, Json)
            ?? throw new InvalidDataException("Invalid checkpoint envelope.");
    }
}
