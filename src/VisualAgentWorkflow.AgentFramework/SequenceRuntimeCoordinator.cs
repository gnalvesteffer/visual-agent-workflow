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
    private readonly Dictionary<Guid, string> completedResults = [];
    private readonly object gate = new();
    private SequenceTerminalState? terminalState;

    public SequenceRuntimeCoordinator(Guid sequenceInvocationId, string sequenceNodeId,
        IReadOnlyList<string> outputIds, IReadOnlyDictionary<string, string> connectionIds,
        bool allowSingleOutput = false)
    {
        if (sequenceInvocationId == Guid.Empty) throw new ArgumentException("A sequence invocation ID is required.", nameof(sequenceInvocationId));
        if (string.IsNullOrWhiteSpace(sequenceNodeId)) throw new ArgumentException("A sequence node ID is required.", nameof(sequenceNodeId));
        ArgumentNullException.ThrowIfNull(connectionIds);
        if (outputIds.Any(outputId => !connectionIds.ContainsKey(outputId)))
            throw new ArgumentException("Every sequence output must have a connection identity.", nameof(connectionIds));

        this.sequenceInvocationId = sequenceInvocationId;
        this.sequenceNodeId = sequenceNodeId;
        this.connectionIds = new Dictionary<string, string>(connectionIds, StringComparer.Ordinal);
        ledger = new SequenceActivationLedger(outputIds, allowSingleOutput);
        events.Add(new SequenceRuntimeEvent(ExecutionEventKinds.SequenceStarted, sequenceNodeId, sequenceInvocationId, null, null, null, null));
    }

    public IReadOnlyList<SequenceRuntimeEvent> Events
    {
        get { lock (gate) return events.ToArray(); }
    }

    public Guid SequenceInvocationId => sequenceInvocationId;

    public string SequenceNodeId => sequenceNodeId;

    public SequenceTerminalState? TerminalState
    {
        get { lock (gate) return terminalState; }
    }

    private int emittedEventCount;

    /// <summary>Returns lifecycle facts not yet sent through the production recorder.</summary>
    public IReadOnlyList<SequenceRuntimeEvent> DrainEvents()
    {
        lock (gate)
        {
            var pending = events.Skip(emittedEventCount).ToArray();
            emittedEventCount = events.Count;
            return pending;
        }
    }

    public bool IsTerminal => ledger.IsTerminal;

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
        lock (gate)
        {
            var snapshot = ledger.DispatchNext(activationId);
            Add(new(ExecutionEventKinds.SequenceOutputDispatched, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
                snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));
            return snapshot;
        }
    }

    public SequenceActivationSnapshot Complete(Guid activationId, SequenceTerminalState terminalState, string? result = null)
    {
        lock (gate)
        {
            var existing = ledger.Snapshot.FirstOrDefault(item => item.ActivationId == activationId);
            if (existing is not null && existing.State != SequenceActivationState.Dispatched)
                return existing;
            var snapshot = ledger.RecordTerminal(activationId, terminalState);
            if (terminalState == SequenceTerminalState.Completed && result is not null)
                completedResults[activationId] = result;
            if (terminalState != SequenceTerminalState.Completed) this.terminalState ??= terminalState;
            var terminalKind = terminalState switch
            {
                SequenceTerminalState.Completed => ExecutionEventKinds.SequenceOutputCompleted,
                SequenceTerminalState.Failed => ExecutionEventKinds.SequenceOutputFailed,
                SequenceTerminalState.Cancelled => ExecutionEventKinds.SequenceOutputCancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(terminalState))
            };
            Add(new(terminalKind, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
                snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));

            var settled = terminalState == SequenceTerminalState.Completed ? [] : ledger.CancelAll();
            foreach (var cancelled in settled.Where(item => item.State == SequenceActivationState.Cancelled))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled && evt.OutputId == cancelled.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputCancelled, sequenceNodeId, sequenceInvocationId, cancelled.OutputId,
                        cancelled.Ordinal, cancelled.ActivationId, connectionIds[cancelled.OutputId]));
            foreach (var skipped in settled.Where(item => item.State == SequenceActivationState.Skipped))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId, skipped.OutputId,
                        skipped.Ordinal, null, connectionIds[skipped.OutputId]));

            if (ledger.IsTerminal)
            {
                var sequenceTerminalKind = this.terminalState is null
                    ? ExecutionEventKinds.SequenceCompleted
                    : this.terminalState == SequenceTerminalState.Failed ? ExecutionEventKinds.SequenceFailed : ExecutionEventKinds.SequenceCancelled;
                Add(new(sequenceTerminalKind, sequenceNodeId, sequenceInvocationId, null, null, null, null));
            }
            return snapshot;
        }
    }

    public bool IsActivationTerminal(Guid activationId) =>
        ledger.Snapshot.Any(item => item.ActivationId == activationId && item.State is not SequenceActivationState.Dispatched);

    public string? DeterministicResult
    {
        get
        {
            lock (gate)
            {
                var completed = ledger.Snapshot
                    .Where(item => item.State == SequenceActivationState.Completed && item.ActivationId is { } activationId &&
                        completedResults.ContainsKey(activationId))
                    .OrderBy(item => item.Ordinal)
                    .FirstOrDefault();
                return completed?.ActivationId is { } id ? completedResults[id] : null;
            }
        }
    }

    public void CancelPending()
    {
        lock (gate)
        {
            if (ledger.IsTerminal) return;
            var cancelled = ledger.CancelAll();
            terminalState ??= SequenceTerminalState.Cancelled;
            foreach (var active in cancelled.Where(item => item.State == SequenceActivationState.Cancelled))
                Add(new(ExecutionEventKinds.SequenceOutputCancelled, sequenceNodeId, sequenceInvocationId, active.OutputId,
                    active.Ordinal, active.ActivationId, connectionIds[active.OutputId]));
            foreach (var skipped in cancelled.Where(item => item.State == SequenceActivationState.Skipped))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId, skipped.OutputId,
                        skipped.Ordinal, null, connectionIds[skipped.OutputId]));
            if (ledger.IsTerminal)
                Add(new(ExecutionEventKinds.SequenceCancelled, sequenceNodeId, sequenceInvocationId, null, null, null, null));
        }
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
