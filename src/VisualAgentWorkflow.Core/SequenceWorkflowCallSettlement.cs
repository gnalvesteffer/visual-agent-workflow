namespace VisualAgentWorkflow.Core;

/// <summary>Identifies the child revision bound to one Sequence WorkflowCall path.</summary>
public sealed record SequenceWorkflowCallIdentity(
    string OutputId,
    string CallNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

/// <summary>Child lifecycle states used by the bounded Sequence WorkflowCall barrier.</summary>
public enum SequenceWorkflowCallSettlementState
{
    Declared,
    Running,
    Completed,
    Failed,
    Cancelled,
    Unknown,
    OutputSettled
}

/// <summary>
/// Core-owned state machine for one call-bearing Sequence output. It prevents duplicate and
/// late child outcomes from settling the parent output twice.
/// </summary>
public sealed class SequenceWorkflowCallSettlement
{
    private readonly object gate = new();
    private SequenceWorkflowCallSettlementState state = SequenceWorkflowCallSettlementState.Declared;

    public SequenceWorkflowCallSettlement(SequenceWorkflowCallIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.OutputId) || string.IsNullOrWhiteSpace(identity.CallNodeId) ||
            string.IsNullOrWhiteSpace(identity.ChildWorkflowId) || identity.ChildRevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.ChildContentHash))
            throw new ArgumentException("A call settlement requires a complete child identity.", nameof(identity));
    }

    public SequenceWorkflowCallIdentity Identity { get; }

    public SequenceWorkflowCallSettlementState State
    {
        get { lock (gate) return state; }
    }

    public bool TryStart()
    {
        lock (gate)
        {
            if (state != SequenceWorkflowCallSettlementState.Declared) return false;
            state = SequenceWorkflowCallSettlementState.Running;
            return true;
        }
    }

    public bool TrySettleChild(SequenceWorkflowCallSettlementState outcome)
    {
        if (outcome is not (SequenceWorkflowCallSettlementState.Completed or
            SequenceWorkflowCallSettlementState.Failed or SequenceWorkflowCallSettlementState.Cancelled or
            SequenceWorkflowCallSettlementState.Unknown))
            throw new ArgumentOutOfRangeException(nameof(outcome));

        lock (gate)
        {
            if (state != SequenceWorkflowCallSettlementState.Running) return false;
            state = outcome;
            return true;
        }
    }

    public bool TrySettleOutput()
    {
        lock (gate)
        {
            if (state is not (SequenceWorkflowCallSettlementState.Completed or
                SequenceWorkflowCallSettlementState.Failed or SequenceWorkflowCallSettlementState.Cancelled or
                SequenceWorkflowCallSettlementState.Unknown)) return false;
            state = SequenceWorkflowCallSettlementState.OutputSettled;
            return true;
        }
    }
}
