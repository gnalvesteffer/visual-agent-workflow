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

    private VisualAgentWorkflow.Core.WorkflowRunReservation? reservation;

    /// <summary>
    /// Counts this owner's outstanding tickets in the run reservation (Decision 0070), so a nested iterator that shares
    /// the reservation sees one dispatch-ticket pool. The pool is unbounded unless the reservation sets a cap.
    /// </summary>
    public void BindReservation(VisualAgentWorkflow.Core.WorkflowRunReservation? runReservation) =>
        reservation = runReservation;

    private static readonly VisualAgentWorkflow.Core.WorkflowReservationRequest OneTicket = new(DispatchTickets: 1);

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

    private sealed record ScopeWaiter(string ScopeId, Guid? ExceptTicketId, TaskCompletionSource Source, bool StartedOnly = false);

    private readonly List<ScopeWaiter> scopeWaiters = [];

    private bool HasUnsettledIn(string scopeId, Guid? exceptTicketId, bool startedOnly = false) =>
        tickets.Any(pair => pair.Value.ScopeId == scopeId && pair.Key != exceptTicketId &&
            pair.Value.Slots.Values.Any(state => startedOnly ? state == TicketState.Started : state != TicketState.Settled));

    /// <summary>True when work admitted in the scope, other than the excepted ticket, is not yet settled.</summary>
    public bool HasUnsettledWork(string? scopeId, Guid? exceptTicketId)
    {
        if (scopeId is null) return false;
        lock (sync) return HasUnsettledIn(scopeId, exceptTicketId);
    }

    /// <summary>
    /// Like <see cref="WhenScopeSettledAsync"/> but ignores work that is admitted and has not started: a caller that
    /// shares a superstep with that work cannot wait for it, because it begins only in the next superstep.
    /// </summary>
    public Task WhenScopeStartedWorkSettledAsync(string? scopeId, Guid? exceptTicketId, CancellationToken cancellationToken = default) =>
        WhenScopeSettledCoreAsync(scopeId, exceptTicketId, startedOnly: true, cancellationToken);

    /// <summary>
    /// Completes when no work admitted in <paramref name="scopeId"/> (other than <paramref name="exceptTicketId"/>) is
    /// unsettled, i.e. after the last such slot settles or its ticket is revoked. Completes immediately when nothing
    /// is outstanding or the scope is null (the unframed root). Waiters complete asynchronously, outside the lock.
    /// </summary>
    public Task WhenScopeSettledAsync(string? scopeId, Guid? exceptTicketId, CancellationToken cancellationToken = default) =>
        WhenScopeSettledCoreAsync(scopeId, exceptTicketId, startedOnly: false, cancellationToken);

    private Task WhenScopeSettledCoreAsync(string? scopeId, Guid? exceptTicketId, bool startedOnly, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scopeId is null) return Task.CompletedTask;
        ScopeWaiter waiter;
        lock (sync)
        {
            if (!HasUnsettledIn(scopeId, exceptTicketId, startedOnly)) return Task.CompletedTask;
            waiter = new ScopeWaiter(scopeId, exceptTicketId,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), startedOnly);
            scopeWaiters.Add(waiter);
        }
        if (!cancellationToken.CanBeCanceled) return waiter.Source.Task;
        var registration = cancellationToken.Register(() =>
        {
            lock (sync) scopeWaiters.Remove(waiter);
            waiter.Source.TrySetCanceled(cancellationToken);
        });
        return waiter.Source.Task.ContinueWith(task => { registration.Dispose(); return task; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Caller holds <c>sync</c>. Removes satisfied waiters; the caller completes them after releasing the lock.</summary>
    private List<ScopeWaiter>? TakeSatisfiedWaiters()
    {
        if (scopeWaiters.Count == 0) return null;
        var ready = scopeWaiters.Where(waiter => !HasUnsettledIn(waiter.ScopeId, waiter.ExceptTicketId, waiter.StartedOnly)).ToList();
        if (ready.Count == 0) return null;
        foreach (var waiter in ready) scopeWaiters.Remove(waiter);
        return ready;
    }

    private static void Release(List<ScopeWaiter>? ready)
    {
        if (ready is null) return;
        foreach (var waiter in ready) waiter.Source.TrySetResult();
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
        if (reservation is { } ticketPool && !ticketPool.TryReserve(null, OneTicket))
            throw new InvalidOperationException("The run's dispatch-ticket reservation is exhausted.");
        lock (sync) tickets.Add(id, new TicketEntry(scopeId, targets.ToDictionary(target => target,
            _ => TicketState.Admitted, StringComparer.Ordinal)));
        try
        {
            foreach (var target in targets) onAdmitted?.Invoke(new Ticket(id, target));
            return id;
        }
        catch
        {
            List<ScopeWaiter>? ready;
            lock (sync)
            {
                tickets.Remove(id);
                ready = TakeSatisfiedWaiters();
            }
            reservation?.Release(null, OneTicket);
            Release(ready);
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
        List<ScopeWaiter>? ready;
        lock (sync)
        {
            if (!tickets.TryGetValue(id, out var entry) || !entry.Slots.TryGetValue(nodeId, out var state) ||
                state != TicketState.Started)
                throw new InvalidOperationException($"Node '{nodeId}' settled a missing or inactive graph-dispatch ticket.");
            entry.Slots[nodeId] = TicketState.Settled;
            ready = TakeSatisfiedWaiters();
            if (entry.Slots.Values.All(slot => slot == TicketState.Settled)) reservation?.Release(null, OneTicket);
        }
        Release(ready);
    }

    /// <summary>Withdraws a ticket whose send failed; legal only while no receiver has started.</summary>
    public void Revoke(Guid ticketId)
    {
        List<ScopeWaiter>? ready;
        lock (sync)
        {
            if (!tickets.TryGetValue(ticketId, out var entry) || entry.Slots.Values.Any(state => state != TicketState.Admitted))
                throw new InvalidOperationException("Only an unsent graph-dispatch ticket can be revoked.");
            tickets.Remove(ticketId);
            ready = TakeSatisfiedWaiters();
            reservation?.Release(null, OneTicket);
        }
        Release(ready);
    }

    public void EnsureQuiescent()
    {
        lock (sync)
            if (tickets.Values.Any(entry => entry.Slots.Values.Any(state => state != TicketState.Settled)))
                throw new InvalidOperationException("The Agent Framework workflow returned with unsettled graph-dispatch tickets.");
    }
}
