using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Non-persisted proof coordinator for the future Sequence runtime.
/// It gives each output lifecycle fact stable invocation, ordinal, and connection identity.
/// </summary>
internal sealed class SequenceRuntimeCoordinator
{
    private readonly SequenceActivationLedger ledger;
    private readonly Guid sequenceInvocationId;
    private readonly string sequenceNodeId;
    private readonly IReadOnlyDictionary<string, string> connectionIds;
    private readonly List<SequenceRuntimeEvent> events = [];
    private readonly object gate = new();

    public SequenceRuntimeCoordinator(Guid sequenceInvocationId, string sequenceNodeId,
        IReadOnlyList<string> outputIds, IReadOnlyDictionary<string, string> connectionIds)
    {
        if (sequenceInvocationId == Guid.Empty) throw new ArgumentException("A sequence invocation ID is required.", nameof(sequenceInvocationId));
        if (string.IsNullOrWhiteSpace(sequenceNodeId)) throw new ArgumentException("A sequence node ID is required.", nameof(sequenceNodeId));
        ArgumentNullException.ThrowIfNull(connectionIds);
        if (outputIds.Any(outputId => !connectionIds.ContainsKey(outputId)))
            throw new ArgumentException("Every sequence output must have a connection identity.", nameof(connectionIds));

        this.sequenceInvocationId = sequenceInvocationId;
        this.sequenceNodeId = sequenceNodeId;
        this.connectionIds = new Dictionary<string, string>(connectionIds, StringComparer.Ordinal);
        ledger = new SequenceActivationLedger(outputIds);
        events.Add(new SequenceRuntimeEvent(ExecutionEventKinds.SequenceStarted, sequenceNodeId, sequenceInvocationId, null, null, null, null));
    }

    public IReadOnlyList<SequenceRuntimeEvent> Events
    {
        get { lock (gate) return events.ToArray(); }
    }

    public IReadOnlyList<ExecutionEvent> ToExecutionEvents(Guid runId, Guid revisionId, long firstSequence,
        DateTimeOffset recordedAt)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (revisionId == Guid.Empty) throw new ArgumentException("A revision ID is required.", nameof(revisionId));

        return Events.Select((runtimeEvent, index) =>
        {
            var evidence = new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId, runtimeEvent.SequenceNodeId,
                runtimeEvent.OutputId, runtimeEvent.Ordinal, runtimeEvent.ActivationId, runtimeEvent.ConnectionId);
            return new ExecutionEvent(runId, revisionId, checked(firstSequence + index), recordedAt,
                runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                runtimeEvent.ConnectionId, SequenceEvidence.Serialize(evidence));
        }).ToArray();
    }

    public SequenceActivationSnapshot DispatchNext(Guid activationId)
    {
        var snapshot = ledger.DispatchNext(activationId);
        Add(new(ExecutionEventKinds.SequenceOutputDispatched, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
            snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));
        return snapshot;
    }

    public SequenceActivationSnapshot Complete(Guid activationId, SequenceTerminalState terminalState)
    {
        var snapshot = ledger.RecordTerminal(activationId, terminalState);
        var terminalKind = terminalState switch
        {
            SequenceTerminalState.Completed => ExecutionEventKinds.SequenceOutputCompleted,
            SequenceTerminalState.Failed => ExecutionEventKinds.SequenceOutputFailed,
            SequenceTerminalState.Cancelled => ExecutionEventKinds.SequenceOutputCancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(terminalState))
        };
        Add(new(terminalKind, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
            snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));

        foreach (var skipped in ledger.Snapshot.Where(item => item.State == SequenceActivationState.Skipped))
            if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId, skipped.OutputId,
                    skipped.Ordinal, null, connectionIds[skipped.OutputId]));

        if (ledger.IsTerminal)
        {
            var sequenceTerminalKind = terminalState == SequenceTerminalState.Completed
                ? ExecutionEventKinds.SequenceCompleted
                : terminalState == SequenceTerminalState.Failed ? ExecutionEventKinds.SequenceFailed : ExecutionEventKinds.SequenceCancelled;
            Add(new(sequenceTerminalKind, sequenceNodeId, sequenceInvocationId, null, null, null, null));
        }
        return snapshot;
    }

    private void Add(SequenceRuntimeEvent runtimeEvent)
    {
        lock (gate) events.Add(runtimeEvent);
    }
}

internal sealed record SequenceRuntimeEvent(
    string Kind,
    string SequenceNodeId,
    Guid SequenceInvocationId,
    string? OutputId,
    int? Ordinal,
    Guid? ActivationId,
    string? ConnectionId);
