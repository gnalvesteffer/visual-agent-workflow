using System.Collections.Concurrent;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

internal sealed class WorkflowLoopExecutionBudget
{
    private readonly WorkflowRunReservation reservation;
    private readonly ConcurrentDictionary<string, byte> loopScopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loopScopedEvidenceScopes = new(StringComparer.Ordinal);

    public WorkflowLoopExecutionBudget(int? maximumEvidenceEvents = null)
        : this(new WorkflowRunReservation(maximumEvidenceEvents: maximumEvidenceEvents))
    {
    }

    /// <summary>A view over the run reservation's evidence-event pool; the pool is shared with every other view.</summary>
    internal WorkflowLoopExecutionBudget(WorkflowRunReservation reservation) =>
        this.reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));

    public WorkflowRunReservation Reservation => reservation;
    public int MaximumEvidenceEvents => reservation.MaximumEvidenceEvents;

    public void RegisterLoopScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        loopScopes.TryAdd(scopeId, 0);
    }

    public bool TryReserveEvidenceEvent(string? scopeId)
    {
        if (!IsLoopScoped(scopeId)) return true;
        return reservation.TryReserve(null, new WorkflowReservationRequest(EvidenceEvents: 1));
    }

    private bool IsLoopScoped(string? scopeId)
    {
        if (string.IsNullOrEmpty(scopeId)) return false;
        if (loopScopedEvidenceScopes.ContainsKey(scopeId)) return true;

        foreach (var loopScope in loopScopes.Keys)
        {
            if (scopeId == loopScope || scopeId.StartsWith(loopScope + "/", StringComparison.Ordinal))
            {
                loopScopedEvidenceScopes.TryAdd(scopeId, 0);
                return true;
            }
        }
        return false;
    }
}

internal sealed class WorkflowLoopPassBudget
{
    private readonly WorkflowRunReservation reservation;

    public WorkflowLoopPassBudget(int? maximumBodyPasses = null)
        : this(new WorkflowRunReservation(maximumBodyPasses))
    {
    }

    /// <summary>A view over the run reservation's body-pass pool; the pool is shared with every other view.</summary>
    internal WorkflowLoopPassBudget(WorkflowRunReservation reservation) =>
        this.reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));

    public WorkflowRunReservation Reservation => reservation;

    public bool TryReserveBodyPass() => reservation.TryReserve(null, new WorkflowReservationRequest(BodyPasses: 1));
}

/// <summary>
/// The one run-level budget object (Decision 0070, atomic shared reservation). The root run creates it; child workflow
/// runs, the Sequence runtime and any future nested iterator inherit the same instance, so passes, loop-scoped evidence
/// events, dispatch tickets and per-owner accounts are drawn from one atomic <see cref="WorkflowRunReservation"/>.
/// </summary>
internal sealed class WorkflowRunBudget
{
    public WorkflowRunBudget(int? maximumBodyPasses = null, int? maximumEvidenceEvents = null,
        int? maximumDispatchTickets = null)
        : this(new WorkflowRunReservation(maximumBodyPasses, maximumEvidenceEvents, maximumDispatchTickets))
    {
    }

    public WorkflowRunBudget(WorkflowRunReservation reservation)
    {
        Reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
        PassBudget = new WorkflowLoopPassBudget(reservation);
        EvidenceBudget = new WorkflowLoopExecutionBudget(reservation);
    }

    public WorkflowRunReservation Reservation { get; }
    public WorkflowLoopPassBudget PassBudget { get; }
    public WorkflowLoopExecutionBudget EvidenceBudget { get; }
}
