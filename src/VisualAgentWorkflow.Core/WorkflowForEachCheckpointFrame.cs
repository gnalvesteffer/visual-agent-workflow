using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// Composite checkpoint frame for the opt-in For Each first slice (decision 0070). It binds the native state, the
/// For Each cursor with its stored items, and the exact evidence prefix. It is not an alternate scheduler and it
/// is separate from <see cref="WorkflowCheckpointFrame"/> so Loop frames keep their exact bytes and rules.
/// </summary>
public sealed record WorkflowForEachCheckpointFrame(
    string FrameVersion,
    string WorkflowSchemaVersion,
    Guid RunId,
    Guid RevisionId,
    string ContentHash,
    string NativePayload,
    ForEachCheckpointCursor Cursor,
    long EvidenceCursor,
    string EvidencePrefixHash,
    string NativePayloadHash,
    string? FrameHash)
{
    public const string CurrentFrameVersion = "foreach-1.0";
    private const int MaximumNativePayloadBytes = 1_048_576;

    public static WorkflowForEachCheckpointFrame Create(WorkflowRevision revision, Guid runId, string nativePayload,
        ForEachCheckpointCursor cursor, IReadOnlyList<ExecutionEvent> evidence)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativePayload);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(evidence);
        var frame = new WorkflowForEachCheckpointFrame(CurrentFrameVersion, revision.Definition.SchemaVersion, runId,
            revision.RevisionId, revision.ContentHash, nativePayload, cursor, evidence.Count,
            WorkflowCheckpointFrame.ComputeEvidenceHash(evidence), Hash(nativePayload), null);
        return frame with { FrameHash = frame.ComputeFrameHash() };
    }

    public string ComputeFrameHash() => Hash(JsonSerializer.Serialize(this with { FrameHash = null }));

    public void Validate(WorkflowRevision revision, IReadOnlyList<ExecutionEvent> evidence)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(evidence);
        if (FrameVersion != CurrentFrameVersion || WorkflowSchemaVersion != revision.Definition.SchemaVersion ||
            !WorkflowCheckpointShape.UsesForEachState(revision.Definition) ||
            RunId == Guid.Empty || RunId != evidence.FirstOrDefault()?.RunId || RevisionId != revision.RevisionId ||
            !string.Equals(ContentHash, revision.ContentHash, StringComparison.Ordinal) ||
            !WorkflowJson.MatchesHash(revision.Definition, ContentHash) ||
            NativePayload is null || Encoding.UTF8.GetByteCount(NativePayload) > MaximumNativePayloadBytes ||
            string.IsNullOrWhiteSpace(NativePayload) || !IsSha256(NativePayloadHash) ||
            !string.Equals(NativePayloadHash, Hash(NativePayload), StringComparison.Ordinal) ||
            !IsSha256(FrameHash) || !string.Equals(FrameHash, ComputeFrameHash(), StringComparison.Ordinal))
            throw new InvalidDataException("The For Each checkpoint frame identity, bounds, or integrity is invalid.");

        using (var native = JsonDocument.Parse(NativePayload, new JsonDocumentOptions { MaxDepth = 32 }))
            if (native.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The For Each checkpoint native state must be a JSON object.");

        if (EvidenceCursor < 1 || EvidenceCursor != evidence.Count ||
            evidence.Select((evt, index) => (evt, index)).Any(item => item.evt is null ||
                item.evt.Sequence != item.index + 1 || item.evt.RunId != RunId || item.evt.RevisionId != RevisionId))
            throw new InvalidDataException("The For Each checkpoint evidence cursor is not a contiguous run prefix.");
        if (!string.Equals(EvidencePrefixHash, WorkflowCheckpointFrame.ComputeEvidenceHash(evidence), StringComparison.Ordinal))
            throw new InvalidDataException("The For Each checkpoint evidence prefix hash is invalid.");

        ValidateCursor(revision.Definition, Cursor, evidence);
    }

    /// <summary>
    /// Returns the canonical items of the For Each's constant array literal, or throws when the definition is not
    /// the admitted first-slice shape (the array input must come directly from an Array literal node).
    /// </summary>
    public static string EvaluateConstantItems(WorkflowDefinition definition, string forEachNodeId)
    {
        var source = definition.Connections.Values.SingleOrDefault(edge =>
            edge.Target.NodeId == forEachNodeId && edge.Target.PortId == "array");
        if (source is null || !definition.Nodes.TryGetValue(source.Source.NodeId, out var literal) ||
            literal?.Type != NodeTypes.ArrayLiteral ||
            !WorkflowArrayLiteral.TryParse(literal.ArrayLiteral, out var values, out _))
            throw new InvalidDataException("The For Each checkpoint array is not a constant array literal.");
        return JsonSerializer.Serialize(values);
    }

    private static void ValidateCursor(WorkflowDefinition definition, ForEachCheckpointCursor cursor,
        IReadOnlyList<ExecutionEvent> evidence)
    {
        if (cursor is null || string.IsNullOrWhiteSpace(cursor.ItemsJson) || !IsSha256(cursor.ItemsHash) ||
            definition.Nodes.Count(pair => pair.Value?.Type == NodeTypes.ForEach) != 1 ||
            !definition.Nodes.TryGetValue(cursor.ForEachNodeId ?? "", out var node) || node?.Type != NodeTypes.ForEach)
            throw new InvalidDataException("The For Each checkpoint cursor does not match the admitted topology.");

        string canonical;
        try { canonical = ForEachCheckpointCursor.CanonicalItems(cursor.ItemsJson); }
        catch (JsonException) { throw new InvalidDataException("The For Each checkpoint items are not valid JSON."); }
        if (!string.Equals(canonical, cursor.ItemsJson, StringComparison.Ordinal) ||
            !string.Equals(ForEachCheckpointCursor.HashItems(canonical), cursor.ItemsHash, StringComparison.Ordinal))
            throw new InvalidDataException("The For Each checkpoint items do not match their hash.");
        // The array is a constant, so a recovery re-evaluation must reproduce the stored items exactly.
        if (!string.Equals(EvaluateConstantItems(definition, cursor.ForEachNodeId!), canonical, StringComparison.Ordinal))
            throw new InvalidDataException("The For Each checkpoint items differ from the constant array.");
        int count;
        using (var items = JsonDocument.Parse(canonical)) count = items.RootElement.GetArrayLength();
        if (cursor.Index < 1 || cursor.Index > count || cursor.Index > WorkflowJsonArrayLimits.MaximumItems ||
            cursor.LastEvidenceSequence < 1 || cursor.LastEvidenceSequence > evidence.Count)
            throw new InvalidDataException("The For Each checkpoint cursor is outside its array.");

        var owned = evidence.Where(evt => evt.NodeId == cursor.ForEachNodeId && evt.Kind is
            ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or
            ExecutionEventKinds.LoopStopped).ToArray();
        var anchor = evidence[(int)cursor.LastEvidenceSequence - 1];
        if (anchor.NodeId != cursor.ForEachNodeId || anchor.Kind != ExecutionEventKinds.LoopIterationStarted ||
            owned.Length == 0 || owned[^1].Sequence != anchor.Sequence ||
            owned.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted) != cursor.Index ||
            owned.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted) != cursor.Index - 1 ||
            anchor.Data is null)
            throw new InvalidDataException("The For Each checkpoint cursor is not anchored to its iteration evidence.");
        try
        {
            using var data = JsonDocument.Parse(anchor.Data);
            if (!data.RootElement.TryGetProperty("iteration", out var iteration) || !iteration.TryGetInt32(out var recorded) ||
                recorded != cursor.Index ||
                !data.RootElement.TryGetProperty("maximumIterations", out var maximum) ||
                !maximum.TryGetInt32(out var recordedCount) || recordedCount != count)
                throw new InvalidDataException("The For Each checkpoint cursor does not match the recorded iteration.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The For Each checkpoint iteration evidence is malformed.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
