namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Run-local authority for graph-message admission in the ordinary production builder.</summary>
internal sealed class WorkflowDispatchAdmissionOwner
{
    internal sealed record Ticket(Guid Id, string TargetNodeId);

    private enum TicketState { Admitted, Started, Settled }

    private readonly object sync = new();
    private readonly Dictionary<Guid, (string Target, TicketState State)> tickets = [];
    private readonly Action<Ticket>? onAdmitted;

    public WorkflowDispatchAdmissionOwner(Action<Ticket>? onAdmitted = null) => this.onAdmitted = onAdmitted;

    public Guid Admit(string targetNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        var ticket = new Ticket(Guid.NewGuid(), targetNodeId);
        lock (sync) tickets.Add(ticket.Id, (ticket.TargetNodeId, TicketState.Admitted));
        try
        {
            onAdmitted?.Invoke(ticket);
            return ticket.Id;
        }
        catch
        {
            lock (sync) tickets.Remove(ticket.Id);
            throw;
        }
    }

    public void Begin(Guid? ticketId, string receiverNodeId)
    {
        if (ticketId is not { } id)
            throw new InvalidOperationException($"Node '{receiverNodeId}' started without an admitted graph-dispatch ticket.");
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var ticket) || ticket.State != TicketState.Admitted ||
                !string.Equals(ticket.Target, receiverNodeId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Node '{receiverNodeId}' received a missing, stale, repeated, or misrouted graph-dispatch ticket.");
            tickets[id] = (ticket.Target, TicketState.Started);
        }
    }

    public void Settle(Guid? ticketId, string nodeId)
    {
        if (ticketId is not { } id) return;
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var ticket) || ticket.State != TicketState.Started ||
                !string.Equals(ticket.Target, nodeId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Node '{nodeId}' settled a missing or inactive graph-dispatch ticket.");
            tickets[id] = (ticket.Target, TicketState.Settled);
        }
    }

    public void Revoke(Guid ticketId)
    {
        lock (sync)
        {
            if (!tickets.TryGetValue(ticketId, out var ticket) || ticket.State != TicketState.Admitted)
                throw new InvalidOperationException("Only an unsent graph-dispatch ticket can be revoked.");
            tickets.Remove(ticketId);
        }
    }

    public void EnsureQuiescent()
    {
        lock (sync)
            if (tickets.Values.Any(ticket => ticket.State != TicketState.Settled))
                throw new InvalidOperationException("The Agent Framework workflow returned with unsettled graph-dispatch tickets.");
    }
}
