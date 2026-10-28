using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Versioned, passive evidence for a bounded Blueprint-style Sequence.</summary>
public sealed record SequenceLifecycleEvidence(
    Guid SequenceInvocationId,
    string SequenceNodeId,
    string? OutputId,
    int? Ordinal,
    Guid? ActivationId,
    string? ConnectionId,
    int Version = 1);

public static class SequenceEvidence
{
    private static readonly HashSet<string> LifecycleKinds = new(StringComparer.Ordinal)
    {
        ExecutionEventKinds.SequenceStarted,
        ExecutionEventKinds.SequenceOutputDispatched,
        ExecutionEventKinds.SequenceOutputCompleted,
        ExecutionEventKinds.SequenceOutputFailed,
        ExecutionEventKinds.SequenceOutputCancelled,
        ExecutionEventKinds.SequenceOutputSkipped,
        ExecutionEventKinds.SequenceCompleted,
        ExecutionEventKinds.SequenceFailed,
        ExecutionEventKinds.SequenceCancelled
    };

    public static string Serialize(SequenceLifecycleEvidence evidence) =>
        JsonSerializer.Serialize(evidence, JsonSerializerOptions.Web);

    public static bool TryRead(ExecutionEvent executionEvent, out SequenceLifecycleEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        evidence = null;
        if (!LifecycleKinds.Contains(executionEvent.Kind) || string.IsNullOrWhiteSpace(executionEvent.Data))
            return false;

        try
        {
            evidence = JsonSerializer.Deserialize<SequenceLifecycleEvidence>(executionEvent.Data, JsonSerializerOptions.Web);
            return evidence is { Version: 1 } && evidence.SequenceInvocationId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(evidence.SequenceNodeId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsLifecycleEvent(string kind) => LifecycleKinds.Contains(kind);
}
