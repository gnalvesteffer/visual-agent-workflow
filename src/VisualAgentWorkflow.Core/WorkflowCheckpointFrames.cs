using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Integrity metadata for a future loop-aware checkpoint boundary.</summary>
/// <remarks>
/// This is a proof contract only. The checkpoint host does not admit schema 1.45
/// until the Agent Framework native cursor is proven to round-trip with this frame.
/// </remarks>
public sealed record LoopCheckpointCursor(
    string LoopNodeId,
    string BodyAnchorNodeId,
    int Iteration,
    int MaximumIterations,
    string NextNodeId,
    string NextInputPortId,
    string? SourceNodeId,
    string? SourceOutputPortId,
    long LastLoopEvidenceSequence);

/// <summary>
/// A definition-bound composite checkpoint proof frame. It binds native state,
/// loop position, mutable state, and the exact recorded evidence prefix without
/// making the frame a portable workflow or an alternate scheduler.
/// </summary>
public sealed record WorkflowCheckpointFrame(
    string FrameVersion,
    string WorkflowSchemaVersion,
    Guid RunId,
    Guid RevisionId,
    string ContentHash,
    string NativePayload,
    LoopCheckpointCursor Loop,
    WorkflowVariableSnapshot Variables,
    long EvidenceCursor,
    string EvidencePrefixHash,
    string NativePayloadHash,
    string VariableStateHash,
    string? FrameHash)
{
    public const string CurrentFrameVersion = "2.0-proof";
    private const int MaximumNativePayloadBytes = 1_048_576;

    public static WorkflowCheckpointFrame Create(WorkflowRevision revision, Guid runId,
        string nativePayload, LoopCheckpointCursor loop, WorkflowVariableSnapshot variables,
        IReadOnlyList<ExecutionEvent> evidence)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativePayload);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(evidence);
        var evidenceCursor = evidence.Count;
        var frame = new WorkflowCheckpointFrame(
            CurrentFrameVersion,
            revision.Definition.SchemaVersion,
            runId,
            revision.RevisionId,
            revision.ContentHash,
            nativePayload,
            loop,
            variables,
            evidenceCursor,
            ComputeEvidenceHash(evidence),
            Hash(nativePayload),
            variables.ComputeHash(),
            null);
        return frame with { FrameHash = frame.ComputeFrameHash() };
    }

    public string ComputeFrameHash() => Hash(JsonSerializer.Serialize(this with { FrameHash = null }));

    public void Validate(WorkflowRevision revision, IReadOnlyList<ExecutionEvent> evidence)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(evidence);
        if (FrameVersion != CurrentFrameVersion || WorkflowSchemaVersion != WorkflowSchemaVersions.LoopVariablesGraph ||
            RunId == Guid.Empty || RunId != evidence.FirstOrDefault()?.RunId || RevisionId != revision.RevisionId ||
            !string.Equals(ContentHash, revision.ContentHash, StringComparison.Ordinal) ||
            !WorkflowJson.MatchesHash(revision.Definition, ContentHash) ||
            Encoding.UTF8.GetByteCount(NativePayload) > MaximumNativePayloadBytes ||
            string.IsNullOrWhiteSpace(NativePayload) || !IsSha256(NativePayloadHash) ||
            !string.Equals(NativePayloadHash, Hash(NativePayload), StringComparison.Ordinal) ||
            Variables is null || !IsSha256(VariableStateHash) ||
            !string.Equals(VariableStateHash, Variables.ComputeHash(), StringComparison.Ordinal) ||
            !IsSha256(FrameHash) || !string.Equals(FrameHash, ComputeFrameHash(), StringComparison.Ordinal))
            throw new InvalidDataException("The loop checkpoint frame identity, bounds, or integrity is invalid.");

        using (var native = JsonDocument.Parse(NativePayload, new JsonDocumentOptions { MaxDepth = 32 }))
            if (native.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The loop checkpoint native state must be a JSON object.");

        if (EvidenceCursor < 1 || EvidenceCursor != evidence.Count ||
            evidence.Select((evt, index) => (evt, index)).Any(item => item.evt is null ||
                item.evt.Sequence != item.index + 1 || item.evt.RunId != RunId || item.evt.RevisionId != RevisionId))
            throw new InvalidDataException("The loop checkpoint evidence cursor is not a contiguous run prefix.");
        if (!string.Equals(EvidencePrefixHash, ComputeEvidenceHash(evidence), StringComparison.Ordinal))
            throw new InvalidDataException("The loop checkpoint evidence prefix hash is invalid.");

        _ = WorkflowVariableStore.FromSnapshot(revision.Definition, Variables);
        var variableEvents = evidence.Where(evt => VariableEvidence.IsStateEvent(evt.Kind)).ToArray();
        if (variableEvents.Length > 0 &&
            (!VariableEvidence.TryReconstructPlayback(variableEvents, out var playback) || playback is null ||
             playback.Values.Any(pair => !MatchesSnapshot(Variables, pair.Key, pair.Value))))
            throw new InvalidDataException("The loop checkpoint variable snapshot does not match its evidence prefix.");
        ValidateLoopCursor(revision.Definition, Loop, evidence);
    }

    private static bool MatchesSnapshot(WorkflowVariableSnapshot snapshot, string variableId, VariablePlaybackValue value) =>
        snapshot.Values.FirstOrDefault(entry => entry.VariableId == variableId) is { } entry &&
        string.Equals(entry.DataType, value.DataType, StringComparison.Ordinal) &&
        string.Equals(entry.Value, value.Value, StringComparison.Ordinal) &&
        entry.Version == value.Version;

    public static string ComputeEvidenceHash(IReadOnlyList<ExecutionEvent> evidence) =>
        Hash(JsonSerializer.Serialize(evidence));

    private static void ValidateLoopCursor(WorkflowDefinition definition, LoopCheckpointCursor cursor,
        IReadOnlyList<ExecutionEvent> evidence)
    {
        var analysis = LoopVariableTopologyAnalyzer.Analyze(definition);
        if (!analysis.IsValid || analysis.Topology is null || cursor is null ||
            cursor.LoopNodeId != analysis.Topology.LoopId || cursor.BodyAnchorNodeId != analysis.Topology.BodyAnchorId ||
            cursor.Iteration < 0 || cursor.MaximumIterations is < 1 or > WorkflowLoopLimits.MaximumIterations ||
            definition.Nodes[cursor.LoopNodeId].Loop?.MaximumIterations != cursor.MaximumIterations ||
            !definition.Nodes.ContainsKey(cursor.NextNodeId) ||
            !WorkflowPorts.ExplicitExecutionInputs(definition.Nodes[cursor.NextNodeId]).Contains(cursor.NextInputPortId) ||
            cursor.LastLoopEvidenceSequence < 1 || cursor.LastLoopEvidenceSequence > evidence.Count)
            throw new InvalidDataException("The loop checkpoint cursor does not match the admitted topology.");

        var loopEvent = evidence[(int)cursor.LastLoopEvidenceSequence - 1];
        if (loopEvent.NodeId != cursor.LoopNodeId ||
            loopEvent.Kind is not (ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped))
            throw new InvalidDataException("The loop checkpoint cursor is not anchored to loop evidence.");
        if (loopEvent.Data is null || !TryReadLoopMetadata(loopEvent, out var recordedIteration, out var recordedMaximum) ||
            recordedIteration != cursor.Iteration ||
            recordedMaximum is { } maximum && maximum != cursor.MaximumIterations)
            throw new InvalidDataException("The loop checkpoint cursor does not match the recorded iteration.");
        if (cursor.SourceNodeId is { } sourceNode &&
            (!definition.Nodes.TryGetValue(sourceNode, out var source) || source is null ||
             cursor.SourceOutputPortId is null || !WorkflowPorts.ExplicitExecutionOutputs(source).Contains(cursor.SourceOutputPortId)))
            throw new InvalidDataException("The loop checkpoint source cursor is not a declared execution output.");
        if (cursor.SourceNodeId is null && cursor.SourceOutputPortId is not null)
            throw new InvalidDataException("The loop checkpoint source cursor is incomplete.");
        if (cursor.SourceNodeId is { } sourceId && cursor.SourceOutputPortId is { } sourcePort &&
            definition.ExecutionConnections?.Values.Any(edge =>
                edge.Source.NodeId == sourceId && edge.Source.PortId == sourcePort &&
                edge.Target.NodeId == cursor.NextNodeId && edge.Target.PortId == cursor.NextInputPortId) != true)
            throw new InvalidDataException("The loop checkpoint source cursor does not target the declared next activation.");
    }

    private static bool TryReadLoopMetadata(ExecutionEvent loopEvent, out int iteration, out int? maximumIterations)
    {
        iteration = 0;
        maximumIterations = null;
        try
        {
            using var document = JsonDocument.Parse(loopEvent.Data!);
            var property = loopEvent.Kind == ExecutionEventKinds.LoopStopped ? "iterations" : "iteration";
            if (!document.RootElement.TryGetProperty(property, out var value) ||
                !value.TryGetInt32(out iteration) || iteration < 0)
                return false;
            if (document.RootElement.TryGetProperty("maximumIterations", out var maximum))
            {
                if (!maximum.TryGetInt32(out var parsedMaximum) || parsedMaximum < 1)
                    return false;
                maximumIterations = parsedMaximum;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
