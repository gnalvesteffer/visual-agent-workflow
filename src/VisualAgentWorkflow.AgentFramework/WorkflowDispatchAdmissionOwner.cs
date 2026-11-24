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
    private sealed class TicketEntry(string? scopeId, Dictionary<string, TicketState> slots)
    {
        public string? ScopeId { get; } = scopeId;
        public Dictionary<string, TicketState> Slots { get; } = slots;
    }

    private readonly Dictionary<Guid, TicketEntry> tickets = [];
    private readonly Action<Ticket>? onAdmitted;

    private readonly Action<WorkflowDispatchAdmissionOwner>? probe;

    /// <summary>Optional test seam; child workflow runs report their own admissions to the same observer.</summary>
    public WorkflowDispatchAdmissionOwner(Action<Ticket>? onAdmitted = null,
        Action<WorkflowDispatchAdmissionOwner>? probe = null)
    {
        this.onAdmitted = onAdmitted;
        this.probe = probe;
        probe?.Invoke(this);
    }

    internal Action<Ticket>? Observer => onAdmitted;
    internal Action<WorkflowDispatchAdmissionOwner>? Probe => probe;

    /// <summary>Every receiver slot not yet settled, as "node:State"; empty when the owner is quiescent.</summary>
    public IReadOnlyList<string> Outstanding()
    {
        lock (sync)
            return tickets.Values.SelectMany(entry => entry.Slots.Where(slot => slot.Value != TicketState.Settled)
                .Select(slot => $"{slot.Key}:{slot.Value}")).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Unsettled receiver slots admitted in one activation scope, as "node:State".</summary>
    public IReadOnlyList<string> OutstandingIn(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        lock (sync)
            return tickets.Values.Where(entry => entry.ScopeId == scopeId)
                .SelectMany(entry => entry.Slots.Where(slot => slot.Value != TicketState.Settled)
                    .Select(slot => $"{slot.Key}:{slot.Value}")).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Fails closed when work admitted in <paramref name="scopeId"/> is still unsettled, ignoring the ticket the caller
    /// itself is processing. A null scope (the unframed root) is never checked. An activation frame must not complete
    /// and its successor must not be admitted while its own work is outstanding.
    /// </summary>
    public void EnsureScopeSettled(string? scopeId, Guid? exceptTicketId)
    {
        if (scopeId is null) return;
        lock (sync)
        {
            var outstanding = tickets.Where(pair => pair.Value.ScopeId == scopeId && pair.Key != exceptTicketId)
                .SelectMany(pair => pair.Value.Slots.Where(slot => slot.Value != TicketState.Settled)
                    .Select(slot => $"{slot.Key}:{slot.Value}")).Order(StringComparer.Ordinal).ToArray();
            if (outstanding.Length != 0)
                throw new InvalidOperationException(
                    $"Activation scope '{scopeId}' cannot complete with unsettled admitted work: {string.Join(", ", outstanding)}.");
        }
    }

    public Guid Admit(string targetNodeId, string? scopeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        return Admit([targetNodeId], scopeId);
    }

    /// <summary>
    /// Admits one ticket with a slot per receiver. A non-null <paramref name="scopeId"/> ties the ticket to the
    /// activation frame (Loop pass or iterator item) it was admitted in, so that frame can be checked for settlement.
    /// </summary>
    public Guid Admit(IReadOnlyCollection<string> targetNodeIds, string? scopeId = null)
    {
        ArgumentNullException.ThrowIfNull(targetNodeIds);
        var targets = targetNodeIds.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
            throw new ArgumentException("A graph-dispatch ticket needs at least one receiver.", nameof(targetNodeIds));
        foreach (var target in targets) ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var id = Guid.NewGuid();
        lock (sync) tickets.Add(id, new TicketEntry(scopeId, targets.ToDictionary(target => target,
            _ => TicketState.Admitted, StringComparer.Ordinal)));
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
            if (!tickets.TryGetValue(id, out var entry) || !entry.Slots.TryGetValue(receiverNodeId, out var state) ||
                state != TicketState.Admitted)
                throw new InvalidOperationException($"Node '{receiverNodeId}' received a missing, stale, repeated, or misrouted graph-dispatch ticket.");
            entry.Slots[receiverNodeId] = TicketState.Started;
        }
    }

    public void Settle(Guid? ticketId, string nodeId)
    {
        if (ticketId is not { } id) return;
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var entry) || !entry.Slots.TryGetValue(nodeId, out var state) ||
                state != TicketState.Started)
                throw new InvalidOperationException($"Node '{nodeId}' settled a missing or inactive graph-dispatch ticket.");
            entry.Slots[nodeId] = TicketState.Settled;
        }
    }

    /// <summary>Withdraws a ticket whose send failed; legal only while no receiver has started.</summary>
    public void Revoke(Guid ticketId)
    {
        lock (sync)
        {
            if (!tickets.TryGetValue(ticketId, out var entry) || entry.Slots.Values.Any(state => state != TicketState.Admitted))
                throw new InvalidOperationException("Only an unsent graph-dispatch ticket can be revoked.");
            tickets.Remove(ticketId);
        }
    }

    public void EnsureQuiescent()
    {
        lock (sync)
            if (tickets.Values.Any(entry => entry.Slots.Values.Any(state => state != TicketState.Settled)))
                throw new InvalidOperationException("The Agent Framework workflow returned with unsettled graph-dispatch tickets.");
    }
}
