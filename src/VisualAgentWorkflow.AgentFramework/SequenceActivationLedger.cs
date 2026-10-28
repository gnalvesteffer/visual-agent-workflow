using System.Diagnostics;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// The bounded, in-process accounting model for a future Blueprint-style Sequence.
/// This is intentionally not a workflow node or a persisted contract yet.
/// </summary>
internal sealed class SequenceActivationLedger
{
    private readonly object gate = new();
    private readonly List<Entry> entries;

    public SequenceActivationLedger(IReadOnlyList<string> outputIds)
    {
        ArgumentNullException.ThrowIfNull(outputIds);
        if (outputIds.Count is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(outputIds), "A bounded sequence must declare between 2 and 4 outputs.");
        if (outputIds.Any(string.IsNullOrWhiteSpace) || outputIds.Distinct(StringComparer.Ordinal).Count() != outputIds.Count)
            throw new ArgumentException("Sequence output IDs must be non-empty and unique.", nameof(outputIds));

        entries = outputIds.Select((id, ordinal) => new Entry(id, ordinal)).ToList();
    }

    public IReadOnlyList<SequenceActivationSnapshot> Snapshot
    {
        get
        {
            lock (gate)
                return entries.Select(entry => entry.ToSnapshot()).ToArray();
        }
    }

    public bool IsTerminal
    {
        get
        {
            lock (gate)
                return entries.All(entry => entry.State is SequenceActivationState.Completed or SequenceActivationState.Skipped)
                    || entries.Any(entry => entry.State is SequenceActivationState.Failed or SequenceActivationState.Cancelled);
        }
    }

    public SequenceActivationSnapshot DispatchNext(Guid activationId)
    {
        if (activationId == Guid.Empty)
            throw new ArgumentException("An activation ID is required.", nameof(activationId));

        lock (gate)
        {
            if (IsTerminalUnderLock())
                throw new InvalidOperationException("A terminal sequence cannot dispatch another output.");

            var next = entries.FirstOrDefault(entry => entry.State == SequenceActivationState.Declared)
                ?? throw new InvalidOperationException("All sequence outputs have already been dispatched.");

            if (entries.Any(entry => entry.State == SequenceActivationState.Dispatched))
                throw new InvalidOperationException("The next sequence output cannot be dispatched until the active output reaches a terminal state.");

            next.State = SequenceActivationState.Dispatched;
            next.ActivationId = activationId;
            return next.ToSnapshot();
        }
    }

    public SequenceActivationSnapshot RecordTerminal(Guid activationId, SequenceTerminalState terminalState)
    {
        if (activationId == Guid.Empty)
            throw new ArgumentException("An activation ID is required.", nameof(activationId));
        if (terminalState is not (SequenceTerminalState.Completed or SequenceTerminalState.Failed or SequenceTerminalState.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(terminalState));

        lock (gate)
        {
            var entry = entries.FirstOrDefault(candidate => candidate.ActivationId == activationId)
                ?? throw new InvalidOperationException("The activation is not registered with this sequence.");
            if (entry.State != SequenceActivationState.Dispatched)
                throw new InvalidOperationException("The activation has already reached a terminal state.");

            entry.State = terminalState switch
            {
                SequenceTerminalState.Completed => SequenceActivationState.Completed,
                SequenceTerminalState.Failed => SequenceActivationState.Failed,
                SequenceTerminalState.Cancelled => SequenceActivationState.Cancelled,
                _ => throw new UnreachableException()
            };

            if (terminalState != SequenceTerminalState.Completed)
                foreach (var remaining in entries.Where(candidate => candidate.State == SequenceActivationState.Declared))
                    remaining.State = SequenceActivationState.Skipped;

            return entry.ToSnapshot();
        }
    }

    private bool IsTerminalUnderLock() => entries.All(entry => entry.State is SequenceActivationState.Completed or SequenceActivationState.Skipped)
        || entries.Any(entry => entry.State is SequenceActivationState.Failed or SequenceActivationState.Cancelled);

    private sealed class Entry(string outputId, int ordinal)
    {
        public string OutputId { get; } = outputId;
        public int Ordinal { get; } = ordinal;
        public Guid? ActivationId { get; set; }
        public SequenceActivationState State { get; set; } = SequenceActivationState.Declared;

        public SequenceActivationSnapshot ToSnapshot() => new(OutputId, Ordinal, ActivationId, State);
    }
}

internal enum SequenceActivationState
{
    Declared,
    Dispatched,
    Completed,
    Failed,
    Cancelled,
    Skipped
}

internal enum SequenceTerminalState
{
    Completed,
    Failed,
    Cancelled
}

internal sealed record SequenceActivationSnapshot(
    string OutputId,
    int Ordinal,
    Guid? ActivationId,
    SequenceActivationState State);
