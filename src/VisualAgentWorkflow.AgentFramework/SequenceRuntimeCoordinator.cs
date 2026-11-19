using System.Collections.Immutable;
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
    private readonly IReadOnlyDictionary<string, SequenceWorkflowCallSettlement>? callSettlements;
    private SequenceTerminalState? terminalState;
    private bool terminalEventRecorded;
    private bool breakCompletionRequested;
    private bool admissionStopped;

    public SequenceRuntimeCoordinator(Guid sequenceInvocationId, string sequenceNodeId,
        IReadOnlyList<string> outputIds, IReadOnlyDictionary<string, string> connectionIds,
        bool allowSingleOutput = false,
        IReadOnlyDictionary<string, SequenceWorkflowCallSettlement>? callSettlements = null)
    {
        if (sequenceInvocationId == Guid.Empty) throw new ArgumentException("A sequence invocation ID is required.", nameof(sequenceInvocationId));
        if (string.IsNullOrWhiteSpace(sequenceNodeId)) throw new ArgumentException("A sequence node ID is required.", nameof(sequenceNodeId));
        ArgumentNullException.ThrowIfNull(connectionIds);
        if (outputIds.Any(outputId => !connectionIds.ContainsKey(outputId)))
            throw new ArgumentException("Every sequence output must have a connection identity.", nameof(connectionIds));

        this.sequenceInvocationId = sequenceInvocationId;
        this.sequenceNodeId = sequenceNodeId;
        this.connectionIds = new Dictionary<string, string>(connectionIds, StringComparer.Ordinal);
        this.callSettlements = callSettlements;
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

    public string? AggregateTerminalEventKind
    {
        get
        {
            lock (gate)
                return terminalEventRecorded
                    ? events.LastOrDefault(runtimeEvent => runtimeEvent.Kind is
                        ExecutionEventKinds.SequenceCompleted or ExecutionEventKinds.SequenceBreakCompleted or
                        ExecutionEventKinds.SequenceFailed or ExecutionEventKinds.SequenceCancelled)?.Kind
                    : null;
        }
    }

    public ImmutableArray<SequenceActivationSnapshot> OutputActivations
    {
        get { lock (gate) return [.. ledger.Snapshot]; }
    }

    internal bool MatchesOutputConnection(string outputId, string connectionId) =>
        connectionIds.TryGetValue(outputId, out var expectedConnectionId) &&
        string.Equals(expectedConnectionId, connectionId, StringComparison.Ordinal);

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
                runtimeEvent.OutputId, runtimeEvent.Ordinal, runtimeEvent.ActivationId, runtimeEvent.ConnectionId,
                Reason: runtimeEvent.Reason);
            return new ExecutionEvent(runId, revisionId, checked(firstSequence + index), recordedAt,
                runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                runtimeEvent.ConnectionId, SequenceEvidence.Serialize(evidence));
        }).ToArray();
    }

    public SequenceActivationSnapshot DispatchNext(Guid activationId)
    {
        lock (gate)
        {
            return DispatchNextUnderLock(activationId);
        }
    }

    public SequenceActivationSnapshot? TryDispatchNext(Guid activationId, int maximumActiveOutputs)
    {
        if (activationId == Guid.Empty) throw new ArgumentException("An activation ID is required.", nameof(activationId));
        if (maximumActiveOutputs is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumActiveOutputs));

        lock (gate)
        {
            var snapshot = ledger.Snapshot;
            if (admissionStopped || ledger.IsTerminal ||
                snapshot.Count(item => item.State == SequenceActivationState.Dispatched) >= maximumActiveOutputs ||
                snapshot.All(item => item.State != SequenceActivationState.Declared))
                return null;
            return DispatchNextUnderLock(activationId);
        }
    }

    private SequenceActivationSnapshot DispatchNextUnderLock(Guid activationId)
    {
        var snapshot = ledger.DispatchNext(activationId);
        Add(new(ExecutionEventKinds.SequenceOutputDispatched, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
            snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));
        return snapshot;
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
            if (terminalState != SequenceTerminalState.Completed)
            {
                admissionStopped = true;
                this.terminalState ??= terminalState;
            }
            var terminalKind = terminalState switch
            {
                SequenceTerminalState.Completed => ExecutionEventKinds.SequenceOutputCompleted,
                SequenceTerminalState.Failed => ExecutionEventKinds.SequenceOutputFailed,
                SequenceTerminalState.Cancelled => ExecutionEventKinds.SequenceOutputCancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(terminalState))
            };
            Add(new(terminalKind, sequenceNodeId, sequenceInvocationId, snapshot.OutputId,
                snapshot.Ordinal, activationId, connectionIds[snapshot.OutputId]));

            var settled = terminalState == SequenceTerminalState.Completed ? [] : CancelProjectedPending();
            foreach (var cancelled in settled.Where(item => item.State == SequenceActivationState.Cancelled))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled && evt.OutputId == cancelled.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputCancelled, sequenceNodeId, sequenceInvocationId, cancelled.OutputId,
                        cancelled.Ordinal, cancelled.ActivationId, connectionIds[cancelled.OutputId]));
            foreach (var skipped in settled.Where(item => item.State == SequenceActivationState.Skipped))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId, skipped.OutputId,
                        skipped.Ordinal, null, connectionIds[skipped.OutputId]));

            RecordTerminalIfComplete();
            return snapshot;
        }
    }

    /// <summary>Closes future output admission while allowing dispatched outputs to settle.</summary>
    public IReadOnlyList<SequenceActivationSnapshot> StopAdmission(bool successfulBreak = false)
    {
        lock (gate)
        {
            admissionStopped = true;
            breakCompletionRequested |= successfulBreak;
            var snapshots = ledger.SkipPending();
            foreach (var skipped in snapshots.Where(item => item.State == SequenceActivationState.Skipped))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId,
                        skipped.OutputId, skipped.Ordinal, null, connectionIds[skipped.OutputId]));
            RecordTerminalIfComplete();
            return snapshots;
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
            admissionStopped = true;
            if (ledger.IsTerminal) return;
            var cancelled = CancelProjectedPending();
            terminalState ??= SequenceTerminalState.Cancelled;
            foreach (var active in cancelled.Where(item => item.State == SequenceActivationState.Cancelled))
                Add(new(ExecutionEventKinds.SequenceOutputCancelled, sequenceNodeId, sequenceInvocationId, active.OutputId,
                    active.Ordinal, active.ActivationId, connectionIds[active.OutputId]));
            foreach (var skipped in cancelled.Where(item => item.State == SequenceActivationState.Skipped))
                if (!events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped && evt.OutputId == skipped.OutputId))
                    Add(new(ExecutionEventKinds.SequenceOutputSkipped, sequenceNodeId, sequenceInvocationId, skipped.OutputId,
                        skipped.Ordinal, null, connectionIds[skipped.OutputId]));
            RecordTerminalIfComplete();
        }
    }

    private void RecordTerminalIfComplete()
    {
        if (!ledger.IsTerminal || terminalEventRecorded) return;
        terminalEventRecorded = true;
        var sequenceTerminalKind = terminalState switch
        {
            null when breakCompletionRequested => ExecutionEventKinds.SequenceBreakCompleted,
            null => ExecutionEventKinds.SequenceCompleted,
            SequenceTerminalState.Failed => ExecutionEventKinds.SequenceFailed,
            _ => ExecutionEventKinds.SequenceCancelled
        };
        Add(new(sequenceTerminalKind, sequenceNodeId, sequenceInvocationId, null, null, null, null,
            sequenceTerminalKind == ExecutionEventKinds.SequenceBreakCompleted ? "break" : null));
    }

    private void Add(SequenceRuntimeEvent runtimeEvent)
    {
        lock (gate) events.Add(runtimeEvent);
    }

    /// <summary>
    /// Before cascade-cancelling in-flight outputs, projects each pending output's own call
    /// settlement (settled on the child root event): completed work stays Completed and other
    /// terminal facts keep their state. Only calls still running are cancelled, so the
    /// sequence-level journal agrees with the per-call evidence regardless of collector order.
    /// </summary>
    private IReadOnlyList<SequenceActivationSnapshot> CancelProjectedPending()
    {
        foreach (var pending in ledger.Snapshot
            .Where(item => item.State == SequenceActivationState.Dispatched && item.ActivationId is not null)
            .ToArray())
        {
            if (ProjectSettlement(pending.OutputId) is not { } projectedState) continue;
            var snapshot = ledger.RecordTerminal(pending.ActivationId!.Value, projectedState);
            Add(new(projectedState switch
            {
                SequenceTerminalState.Failed => ExecutionEventKinds.SequenceOutputFailed,
                SequenceTerminalState.Cancelled => ExecutionEventKinds.SequenceOutputCancelled,
                _ => ExecutionEventKinds.SequenceOutputCompleted
            }, sequenceNodeId, sequenceInvocationId, snapshot.OutputId, snapshot.Ordinal, pending.ActivationId, connectionIds[snapshot.OutputId]));
        }
        return ledger.CancelAll();
    }

    private SequenceTerminalState? ProjectSettlement(string outputId)
    {
        if (callSettlements is null || !callSettlements.TryGetValue(outputId, out var settlement))
            return null;
        var outcome = settlement.State == SequenceWorkflowCallSettlementState.OutputSettled
            ? settlement.ChildOutcome
            : settlement.State;
        return outcome switch
        {
            SequenceWorkflowCallSettlementState.Completed => SequenceTerminalState.Completed,
            SequenceWorkflowCallSettlementState.Failed or SequenceWorkflowCallSettlementState.Unknown => SequenceTerminalState.Failed,
            SequenceWorkflowCallSettlementState.Cancelled => SequenceTerminalState.Cancelled,
            _ => null
        };
    }
}

/// <summary>Private typed collector handoff for one terminal Sequence activation region.</summary>
internal sealed record SequenceAggregateCompletionSignal(
    Guid SequenceInvocationId,
    string SequenceNodeId,
    string? ScopeId,
    Guid? FrameId,
    string TerminalEventKind,
    SequenceOutputActivationLineage LastSettledOutput,
    ImmutableArray<SequenceActivationSnapshot> OutputActivations)
{
    public bool Matches(SequenceOutputActivationLineage lineage, Guid? frameId) =>
        SequenceInvocationId == lineage.SequenceInvocationId &&
        SequenceNodeId == lineage.SequenceNodeId &&
        ScopeId == lineage.ScopeId &&
        FrameId == frameId &&
        LastSettledOutput == lineage;

    public static SequenceAggregateCompletionSignal? Create(SequenceRuntimeCoordinator coordinator,
        SequenceOutputActivationLineage lastSettledOutput, Guid? frameId)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(lastSettledOutput);
        if (string.IsNullOrWhiteSpace(lastSettledOutput.OutputId) ||
            string.IsNullOrWhiteSpace(lastSettledOutput.ConnectionId) ||
            lastSettledOutput.ActivationId == Guid.Empty || lastSettledOutput.Ordinal < 0)
            throw new ArgumentException("The aggregate completion signal must retain a settled output lineage.", nameof(lastSettledOutput));
        if (lastSettledOutput.SequenceInvocationId != coordinator.SequenceInvocationId ||
            lastSettledOutput.SequenceNodeId != coordinator.SequenceNodeId)
            throw new InvalidOperationException("The aggregate output lineage belongs to a different Sequence invocation.");

        var terminalKind = coordinator.AggregateTerminalEventKind;
        if (terminalKind is null) return null;
        var outputs = coordinator.OutputActivations;
        if (!coordinator.MatchesOutputConnection(lastSettledOutput.OutputId, lastSettledOutput.ConnectionId) ||
            !outputs.Any(item => item.OutputId == lastSettledOutput.OutputId && item.Ordinal == lastSettledOutput.Ordinal &&
                item.ActivationId == lastSettledOutput.ActivationId))
            throw new InvalidOperationException("The aggregate completion lineage is absent from the settled Sequence snapshot.");

        return new(coordinator.SequenceInvocationId, coordinator.SequenceNodeId, lastSettledOutput.ScopeId,
            frameId, terminalKind, lastSettledOutput, outputs);
    }
}

internal sealed record SequenceOutputActivationLineage(
    Guid SequenceInvocationId,
    string SequenceNodeId,
    string OutputId,
    int Ordinal,
    Guid ActivationId,
    string ConnectionId,
    string? ScopeId);

internal sealed record SequenceRuntimeEvent(
    string Kind,
    string SequenceNodeId,
    Guid SequenceInvocationId,
    string? OutputId,
    int? Ordinal,
    Guid? ActivationId,
    string? ConnectionId,
    string? Reason = null);
