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
    /// <summary>Composite state for the explicitly opted-in deterministic loop checkpoint capability.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowCheckpointFrame? Frame { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableStateHash { get; init; }

    /// <summary>Composite state for the opt-in For Each iteration-boundary checkpoint (decision 0070, first slice).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowForEachCheckpointFrame? ForEachFrame { get; init; }

    /// <summary>
    /// The pass frame id and index in <see cref="NativeWorkflowCheckpoint.ForEach"/> are outside the payload hash, yet
    /// restore keys the pass's item/index outputs by them. They must equal the one in-flight activation frame that the
    /// native payload itself carries, whose item must be the cursor's item at that index.
    /// </summary>
    private void ValidatePassFrameInNativePayload(ForEachCheckpointState state, ForEachCheckpointCursor cursor)
    {
        const string mismatch = "For Each checkpoint pass frame does not match the in-flight frame in the native payload.";
        using var items = JsonDocument.Parse(cursor.ItemsJson);
        var expected = items.RootElement[cursor.Index - 1];
        var expectedItem = expected.ValueKind == JsonValueKind.String ? expected.GetString() : expected.GetRawText();
        using var payload = JsonDocument.Parse(Native.Payload);
        var queued = payload.RootElement.TryGetProperty("runnerData", out var runner) &&
            runner.TryGetProperty("queuedMessages", out var messages) && messages.ValueKind == JsonValueKind.Object
            ? messages.EnumerateObject().SelectMany(entry => entry.Value.EnumerateArray()).ToArray() : [];
        if (queued.Length != 1 ||
            !queued[0].TryGetProperty("message", out var message) || !message.TryGetProperty("value", out var value) ||
            !value.TryGetProperty("activationFrame", out var frame) || frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("id", out var id) || !id.TryGetGuid(out var frameId) || frameId != state.PassFrameId ||
            !frame.TryGetProperty("index", out var index) || !index.TryGetInt32(out var frameIndex) ||
            frameIndex != state.PassIndex || frameIndex != cursor.Index ||
            !frame.TryGetProperty("loopNodeId", out var loop) || loop.GetString() != cursor.ForEachNodeId ||
            frame.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null ||
            !frame.TryGetProperty("item", out var item) || item.GetString() != expectedItem)
            throw new InvalidDataException(mismatch);
    }

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
        WorkflowRunner.ValidateCheckpointGraph(snapshot.Header.Definition, allowLoopCheckpointRecovery: true,
            allowForEachCheckpointRecovery: true);
        var variableState = Native.VariableState;
        if (WorkflowCheckpointShape.UsesForEachState(snapshot.Header.Definition))
        {
            var forEachRevision = WorkflowRevision.Rehydrate(snapshot.Header.RevisionId, snapshot.Header.Definition,
                snapshot.Header.Author, snapshot.Header.ParentRevisionId, snapshot.Header.CreatedAt, snapshot.Header.ContentHash);
            if (ForEachFrame is null || Frame is not null || variableState is not null || VariableStateHash is not null ||
                Native.ForEach is null)
                throw new InvalidDataException("For Each checkpoint state is missing its composite recovery frame or native state.");
            ForEachFrame.Validate(forEachRevision, snapshot.Events.Take((int)Sequence).ToArray());
            var cursor = ForEachFrame.Cursor;
            if (ForEachFrame.EvidenceCursor != Sequence ||
                !string.Equals(ForEachFrame.NativePayload, Native.Payload, StringComparison.Ordinal) ||
                !string.Equals(ForEachFrame.NativePayloadHash, PayloadHash, StringComparison.Ordinal) ||
                Native.ForEach.ForEachNodeId != cursor.ForEachNodeId || Native.ForEach.PassIndex != cursor.Index ||
                Native.ForEach.PassFrameId == Guid.Empty ||
                !string.Equals(ForEachCheckpointCursor.CanonicalItems(Native.ForEach.ItemsJson), cursor.ItemsJson, StringComparison.Ordinal))
                throw new InvalidDataException("For Each checkpoint native state does not match its cursor.");
            ValidatePassFrameInNativePayload(Native.ForEach, cursor);
            return;
        }
        if (Native.ForEach is not null || ForEachFrame is not null)
            throw new InvalidDataException("For Each checkpoint state is not allowed for this workflow.");
        if (WorkflowCheckpointShape.UsesLoopState(snapshot.Header.Definition))
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
        else if (WorkflowCheckpointShape.UsesVariableState(snapshot.Header.Definition))
        {
            if (Frame is not null || variableState is null || VariableStateHash is null || VariableStateHash != variableState.ComputeHash())
                throw new InvalidDataException("Variable checkpoint state is missing or has an invalid integrity hash.");
            _ = WorkflowVariableStore.FromSnapshot(snapshot.Header.Definition, variableState);
        }
        else
        {
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

/// <summary>Transactional checkpoint store with the host's exclusive local ownership capability.</summary>
public interface IWorkflowCheckpointHostStore : IWorkflowCheckpointStore, ILocalRuntimeOwnershipStore { }

public sealed record WorkflowRecoveryAssessment(bool CanResume, string Reason);

public static class WorkflowCheckpointRecovery
{
    /// <summary>Checks whether an uncertain publication already committed this exact native suspension.</summary>
    public static bool IsCommittedSuspension(RunSnapshot snapshot, WorkflowRunCheckpoint? checkpoint,
        NativeWorkflowCheckpoint native)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(native);
        try
        {
            return snapshot.Status == "suspended" && checkpoint is not null &&
                string.Equals(checkpoint.Native.CheckpointId, native.CheckpointId, StringComparison.Ordinal) &&
                string.Equals(checkpoint.Native.Payload, native.Payload, StringComparison.Ordinal) &&
                Assess(snapshot, checkpoint).CanResume;
        }
        catch (Exception error) when (error is not (OperationCanceledException or
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return false;
        }
    }

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
