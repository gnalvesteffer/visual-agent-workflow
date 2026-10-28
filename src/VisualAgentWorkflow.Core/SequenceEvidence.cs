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

    /// <summary>Reconstructs a Sequence prefix using recorded evidence only.</summary>
    public static bool TryReconstructPlayback(IEnumerable<ExecutionEvent> executionEvents,
        out SequencePlaybackSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(executionEvents);
        snapshot = null;
        var events = executionEvents.Where(evt => IsLifecycleEvent(evt.Kind)).OrderBy(evt => evt.Sequence).ToArray();
        if (events.Length == 0) return false;

        SequenceLifecycleEvidence? identity = null;
        var outputs = new Dictionary<int, SequencePlaybackOutput>();
        Guid? activeActivation = null;
        string? activeOutput = null;
        var started = false;
        var terminal = false;
        var status = "running";
        foreach (var executionEvent in events)
        {
            if (!TryRead(executionEvent, out var evidence) || evidence is null || terminal)
                return false;
            if (executionEvent.NodeId != evidence.SequenceNodeId ||
                executionEvent.InvocationId != evidence.SequenceInvocationId ||
                (evidence.ConnectionId is not null && executionEvent.ConnectionId != evidence.ConnectionId))
                return false;
            identity ??= evidence;
            if (identity.SequenceInvocationId != evidence.SequenceInvocationId || identity.SequenceNodeId != evidence.SequenceNodeId)
                return false;
            if (!started && executionEvent.Kind != ExecutionEventKinds.SequenceStarted)
                return false;

            switch (executionEvent.Kind)
            {
                case ExecutionEventKinds.SequenceStarted:
                    if (started || identity.OutputId is not null || outputs.Count != 0) return false;
                    started = true;
                    break;
                case ExecutionEventKinds.SequenceOutputDispatched:
                    if (evidence.OutputId is null || evidence.Ordinal is not { } ordinal || ordinal != outputs.Count ||
                        evidence.ActivationId is not { } activation || evidence.ConnectionId is null || activeActivation is not null)
                        return false;
                    outputs.Add(ordinal, new(evidence.OutputId, ordinal, activation, evidence.ConnectionId, "dispatched"));
                    activeActivation = activation;
                    activeOutput = evidence.OutputId;
                    break;
                case ExecutionEventKinds.SequenceOutputCompleted:
                case ExecutionEventKinds.SequenceOutputFailed:
                case ExecutionEventKinds.SequenceOutputCancelled:
                    if (activeActivation != evidence.ActivationId || activeOutput != evidence.OutputId ||
                        evidence.Ordinal is not { } terminalOrdinal || !outputs.TryGetValue(terminalOrdinal, out var dispatched) ||
                        dispatched.State != "dispatched") return false;
                    var outputState = executionEvent.Kind switch
                    {
                        ExecutionEventKinds.SequenceOutputCompleted => "completed",
                        ExecutionEventKinds.SequenceOutputFailed => "failed",
                        _ => "cancelled"
                    };
                    outputs[terminalOrdinal] = dispatched with { State = outputState };
                    activeActivation = null;
                    activeOutput = null;
                    break;
                case ExecutionEventKinds.SequenceOutputSkipped:
                    if (evidence.OutputId is null || evidence.Ordinal is not { } skippedOrdinal ||
                        evidence.ConnectionId is null || activeActivation is not null || outputs.ContainsKey(skippedOrdinal))
                        return false;
                    outputs.Add(skippedOrdinal, new(evidence.OutputId, skippedOrdinal, null, evidence.ConnectionId, "skipped"));
                    break;
                case ExecutionEventKinds.SequenceCompleted:
                    if (activeActivation is not null || outputs.Count == 0 || outputs.Values.Any(output => output.State != "completed"))
                        return false;
                    status = "completed";
                    terminal = true;
                    break;
                case ExecutionEventKinds.SequenceFailed:
                    if (activeActivation is not null || !outputs.Values.Any(output => output.State == "failed")) return false;
                    status = "failed";
                    terminal = true;
                    break;
                case ExecutionEventKinds.SequenceCancelled:
                    if (activeActivation is not null || !outputs.Values.Any(output => output.State is "cancelled" or "skipped")) return false;
                    status = "cancelled";
                    terminal = true;
                    break;
                default:
                    return false;
            }
        }

        if (identity is null) return false;
        snapshot = new(identity.SequenceInvocationId, identity.SequenceNodeId, status,
            outputs.Values.OrderBy(output => output.Ordinal).ToArray());
        return true;
    }
}

public sealed record SequencePlaybackSnapshot(Guid SequenceInvocationId, string SequenceNodeId, string Status,
    IReadOnlyList<SequencePlaybackOutput> Outputs);

public sealed record SequencePlaybackOutput(string OutputId, int Ordinal, Guid? ActivationId,
    string ConnectionId, string State);
