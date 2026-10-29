using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Trusted local recovery envelope. Native state is runtime-version-specific, never accepted from workflow imports.</summary>
public sealed record WorkflowRunCheckpoint(string SchemaVersion, string RuntimeVersion, Guid RunId, Guid RevisionId,
    string ContentHash, long Sequence, NativeWorkflowCheckpoint Native, string PayloadHash)
{
    /// <summary>Proof-only composite state for the schema-1.45 loop checkpoint seam.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowCheckpointFrame? Frame { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableStateHash { get; init; }

    public static string Hash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    public void Validate(RunSnapshot snapshot)
    {
        if (Native is null || Native.Payload is null || SchemaVersion != "1.0" || RuntimeVersion != WorkflowRunner.CheckpointRuntimeVersion ||
            RunId != snapshot.Header.RunId || RevisionId != snapshot.Header.RevisionId || ContentHash != snapshot.Header.ContentHash ||
            Sequence < 1 || Sequence > snapshot.Events.Length || Native.SessionId != RunId.ToString("D") ||
            string.IsNullOrWhiteSpace(Native.CheckpointId) || Native.CheckpointId.Length > 128 ||
            Native.ParentCheckpointId?.Length > 128 || Encoding.UTF8.GetByteCount(Native.Payload) > 1_048_576 ||
            PayloadHash != Hash(Native.Payload))
            throw new InvalidDataException("Checkpoint version, identity, cursor or integrity mismatch.");
        using var json = JsonDocument.Parse(Native.Payload);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Checkpoint state must be an object.");
        var variableState = Native.VariableState;
        if (snapshot.Header.Definition.SchemaVersion == WorkflowSchemaVersions.LoopVariablesGraph)
        {
            if (Frame is null)
                throw new InvalidDataException("Loop checkpoint state is missing its composite recovery frame.");
            var revision = WorkflowRevision.Rehydrate(snapshot.Header.RevisionId, snapshot.Header.Definition,
                snapshot.Header.Author, snapshot.Header.ParentRevisionId, snapshot.Header.CreatedAt, snapshot.Header.ContentHash);
            if (Sequence > snapshot.Events.Length)
                throw new InvalidDataException("Loop checkpoint state points beyond the recorded evidence prefix.");
            Frame.Validate(revision, snapshot.Events.Take((int)Sequence).ToArray());
            if (Frame.EvidenceCursor != Sequence ||
                !string.Equals(Frame.NativePayload, Native.Payload, StringComparison.Ordinal) ||
                !string.Equals(Frame.NativePayloadHash, PayloadHash, StringComparison.Ordinal) ||
                variableState is null || VariableStateHash is null ||
                VariableStateHash != variableState.ComputeHash() ||
                !string.Equals(Frame.VariableStateHash, VariableStateHash, StringComparison.Ordinal) ||
                Frame.Variables.ComputeHash() != variableState.ComputeHash())
                throw new InvalidDataException("Loop variable checkpoint state is missing or has an invalid integrity hash.");
            _ = WorkflowVariableStore.FromSnapshot(snapshot.Header.Definition, variableState);
        }
        else if (snapshot.Header.Definition.SchemaVersion == WorkflowSchemaVersions.MutableVariablesGraph)
        {
            if (variableState is null || VariableStateHash is null || VariableStateHash != variableState.ComputeHash())
                throw new InvalidDataException("Variable checkpoint state is missing or has an invalid integrity hash.");
            WorkflowRunner.ValidateCheckpointGraph(snapshot.Header.Definition);
            _ = WorkflowVariableStore.FromSnapshot(snapshot.Header.Definition, variableState);
        }
        else
        {
            WorkflowRunner.ValidateCheckpointGraph(snapshot.Header.Definition);
            if (variableState is not null || VariableStateHash is not null || Frame is not null)
                throw new InvalidDataException("Variable checkpoint state is not allowed for this workflow schema.");
        }
    }
}

/// <summary>Optional capability: checkpoint publication and event/state transition share one transaction.</summary>
public interface IWorkflowCheckpointStore : IWorkflowRunStore
{
    RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events, WorkflowRunCheckpoint checkpoint);
    WorkflowRunCheckpoint? GetCheckpoint(Guid runId);
}

public sealed record WorkflowRecoveryAssessment(bool CanResume, string Reason);

public static class WorkflowCheckpointRecovery
{
    public static WorkflowRecoveryAssessment Assess(RunSnapshot snapshot, WorkflowRunCheckpoint? checkpoint)
    {
        try
        {
            if (snapshot.IsTerminal) return new(false, "The run is terminal.");
            if (checkpoint is null) return new(false, "No committed checkpoint exists.");
            checkpoint.Validate(snapshot);
            if (snapshot.Events.Length != checkpoint.Sequence)
                return new(false, "Evidence advanced after the checkpoint; automatic replay is unsafe.");
            if (snapshot.Status != "suspended" || snapshot.Events[^1].Kind != ExecutionEventKinds.Suspended)
                return new(false, "Recovery requires an explicitly saved suspension.");
            var unsettled = WorkflowWorkEvidence.Read(snapshot);
            if (unsettled.TotalUnresolvedWork > 0 || unsettled.HasIncompleteCorrelation)
                return new(false, "Recorded work is unsettled or ambiguously correlated.");
            var started = snapshot.Events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.InvocationId).ToHashSet();
            var completed = snapshot.Events.Where(e => e.Kind == ExecutionEventKinds.NodeCompleted).Select(e => e.InvocationId).ToHashSet();
            if (!started.SetEquals(completed)) return new(false, "An invocation has no recorded completed boundary.");
            return new(true, "A deterministic native checkpoint can be resumed by an exclusive host.");
        }
        // This is a persisted-state trust boundary. Malformed snapshot or checkpoint
        // data must make recovery ineligible, never turn inspection into an exception.
        // Cancellation and process-fatal failures remain outside this normalization.
        catch (Exception error) when (error is not (OperationCanceledException or
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return new(false, "The checkpoint is incompatible or invalid.");
        }
    }
}
