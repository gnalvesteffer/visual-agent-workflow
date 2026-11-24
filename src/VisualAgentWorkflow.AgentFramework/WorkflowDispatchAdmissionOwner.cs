namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Run-local authority for graph-message admission in the ordinary production builder.</summary>
/// <remarks>
/// Agent Framework delivers one returned message to every matching edge, so a ticket carries one slot per
/// distinct receiver. Each receiver must consume its own slot (Admitted, Started, Settled) exactly once.
/// </remarks>
internal sealed class WorkflowDispatchAdmissionOwner
{
    internal sealed record Ticket(Guid Id, string TargetNodeId);

    private enum TicketState { Admitted, Started, Settled }

    private readonly object sync = new();
    private readonly Dictionary<Guid, Dictionary<string, TicketState>> tickets = [];
    private readonly Action<Ticket>? onAdmitted;

    public WorkflowDispatchAdmissionOwner(Action<Ticket>? onAdmitted = null) => this.onAdmitted = onAdmitted;

    public Guid Admit(string targetNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        return Admit([targetNodeId]);
    }

    public Guid Admit(IReadOnlyCollection<string> targetNodeIds)
    {
        ArgumentNullException.ThrowIfNull(targetNodeIds);
        var targets = targetNodeIds.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
            throw new ArgumentException("A graph-dispatch ticket needs at least one receiver.", nameof(targetNodeIds));
        foreach (var target in targets) ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var id = Guid.NewGuid();
        lock (sync) tickets.Add(id, targets.ToDictionary(target => target, _ => TicketState.Admitted,
            StringComparer.Ordinal));
        try
        {
            foreach (var target in targets) onAdmitted?.Invoke(new Ticket(id, target));
            return id;
        }
        catch
        {
            lock (sync) tickets.Remove(id);
            throw;
        }
    }

    public void Begin(Guid? ticketId, string receiverNodeId)
    {
        if (ticketId is not { } id)
            throw new InvalidOperationException($"Node '{receiverNodeId}' started without an admitted graph-dispatch ticket.");
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var slots) || !slots.TryGetValue(receiverNodeId, out var state) ||
                state != TicketState.Admitted)
                throw new InvalidOperationException($"Node '{receiverNodeId}' received a missing, stale, repeated, or misrouted graph-dispatch ticket.");
            slots[receiverNodeId] = TicketState.Started;
        }
    }

    public void Settle(Guid? ticketId, string nodeId)
    {
        if (ticketId is not { } id) return;
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var slots) || !slots.TryGetValue(nodeId, out var state) ||
                state != TicketState.Started)
                throw new InvalidOperationException($"Node '{nodeId}' settled a missing or inactive graph-dispatch ticket.");
            slots[nodeId] = TicketState.Settled;
        }
    }

    /// <summary>Withdraws a ticket whose send failed; legal only while no receiver has started.</summary>
    public void Revoke(Guid ticketId)
    {
        lock (sync)
        {
            if (!tickets.TryGetValue(ticketId, out var slots) || slots.Values.Any(state => state != TicketState.Admitted))
                throw new InvalidOperationException("Only an unsent graph-dispatch ticket can be revoked.");
            tickets.Remove(ticketId);
        }
    }

    public void EnsureQuiescent()
    {
        lock (sync)
            if (tickets.Values.Any(slots => slots.Values.Any(state => state != TicketState.Settled)))
                throw new InvalidOperationException("The Agent Framework workflow returned with unsettled graph-dispatch tickets.");
    }
}
