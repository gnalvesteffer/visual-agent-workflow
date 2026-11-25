namespace VisualAgentWorkflow.Core;

/// <summary>What one atomic reservation draws. Every count is all-or-nothing together (Decision 0070).</summary>
/// <param name="Activations">Item or body-pass activations, drawn from the account's cap.</param>
/// <param name="BodyPasses">Body passes, drawn from the run-wide pass pool.</param>
/// <param name="DispatchTickets">Outstanding graph-dispatch tickets, drawn from the run-wide ticket pool.</param>
/// <param name="EvidenceEvents">Loop-scoped evidence events, drawn from the run-wide event pool.</param>
/// <param name="ArrayEvidenceBytes">Typed-array evidence bytes, drawn from the account's byte cap.</param>
/// <param name="ActivationsExemptFromAccountCap">Counts the activations but does not enforce the account cap
/// (checkpoint restore counts a pass it did not choose to start).</param>
public readonly record struct WorkflowReservationRequest(
    int Activations = 0, int BodyPasses = 0, int DispatchTickets = 0, int EvidenceEvents = 0,
    int ArrayEvidenceBytes = 0, bool ActivationsExemptFromAccountCap = false);

/// <summary>Which pool refused a reservation. Pools are checked in the order listed, first refusal wins.</summary>
public enum WorkflowReservationOutcome
{
    Reserved,
    ActivationsExhausted,
    BodyPassesExhausted,
    DispatchTicketsExhausted,
    EvidenceEventsExhausted,
    ArrayEvidenceExhausted
}

/// <summary>
/// One run-level reservation shared by the ordinary runner, child workflow runs and the Sequence runtime. A single
/// lock covers every pool, so a concurrent reservation never observes (or leaves behind) a partial draw: either all
/// of the requested counts are taken or none is. Pools that are run-wide (passes, evidence events, dispatch tickets)
/// live here; caps that belong to one frame owner (activations, array evidence bytes) live in an
/// <see cref="Account"/> created from the same reservation, so they are counted under the same lock.
/// </summary>
public sealed class WorkflowRunReservation
{
    /// <summary>Per-owner counters. Created by <see cref="CreateAccount"/>; mutated only under the reservation lock.</summary>
    public sealed class Account
    {
        internal Account(long maximumActivations, int maximumArrayEvidenceBytes)
        {
            MaximumActivations = maximumActivations;
            MaximumArrayEvidenceBytes = maximumArrayEvidenceBytes;
        }

        public long MaximumActivations { get; }
        public int MaximumArrayEvidenceBytes { get; }
        internal long ActivationsUsed;
        internal int ArrayEvidenceBytesUsed;
    }

    private readonly object sync = new();
    private readonly int maximumBodyPasses;
    private readonly int maximumEvidenceEvents;
    private readonly int? maximumDispatchTickets;
    private int bodyPasses;
    private int evidenceEvents;
    private int dispatchTickets;
    private int dispatchTicketsPeak;

    /// <param name="maximumBodyPasses">Defaults to <see cref="WorkflowLoopLimits.MaximumTotalBodyPassesPerRun"/>.</param>
    /// <param name="maximumEvidenceEvents">Defaults to <see cref="WorkflowLoopLimits.MaximumScopedEvidenceEventsPerRun"/>.</param>
    /// <param name="maximumDispatchTickets">Outstanding-ticket cap; null means counted but unbounded.</param>
    public WorkflowRunReservation(int? maximumBodyPasses = null, int? maximumEvidenceEvents = null,
        int? maximumDispatchTickets = null)
    {
        this.maximumBodyPasses = maximumBodyPasses ?? WorkflowLoopLimits.MaximumTotalBodyPassesPerRun;
        this.maximumEvidenceEvents = maximumEvidenceEvents ?? WorkflowLoopLimits.MaximumScopedEvidenceEventsPerRun;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.maximumBodyPasses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.maximumEvidenceEvents);
        if (maximumDispatchTickets is { } tickets) ArgumentOutOfRangeException.ThrowIfNegative(tickets);
        this.maximumDispatchTickets = maximumDispatchTickets;
    }

    public int MaximumBodyPasses => maximumBodyPasses;
    public int MaximumEvidenceEvents => maximumEvidenceEvents;
    public int? MaximumDispatchTickets => maximumDispatchTickets;
    public int BodyPassesUsed { get { lock (sync) return bodyPasses; } }
    public int EvidenceEventsUsed { get { lock (sync) return evidenceEvents; } }
    public int DispatchTicketsOutstanding { get { lock (sync) return dispatchTickets; } }

    /// <summary>The most dispatch tickets that were ever outstanding together; a diagnostic for shared-budget proofs.</summary>
    public int DispatchTicketsPeak { get { lock (sync) return dispatchTicketsPeak; } }

    public Account CreateAccount(long maximumActivations, int maximumArrayEvidenceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumActivations);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumArrayEvidenceBytes);
        return new Account(maximumActivations, maximumArrayEvidenceBytes);
    }

    public long ActivationsUsed(Account account) { lock (sync) return account.ActivationsUsed; }
    public int ArrayEvidenceBytesUsed(Account account) { lock (sync) return account.ArrayEvidenceBytesUsed; }

    /// <summary>
    /// Draws every requested count or none. <paramref name="account"/> may be null only when the request draws no
    /// account-scoped counts (activations or array bytes).
    /// </summary>
    public bool TryReserve(Account? account, WorkflowReservationRequest request, out WorkflowReservationOutcome outcome)
    {
        Validate(account, request);
        lock (sync)
        {
            outcome = Check(account, request);
            if (outcome != WorkflowReservationOutcome.Reserved) return false;
            Apply(account, request, 1);
            return true;
        }
    }

    public bool TryReserve(Account? account, WorkflowReservationRequest request) => TryReserve(account, request, out _);

    /// <summary>
    /// Returns a previous reservation. Releasing more than was drawn is a programming error and throws without
    /// changing any pool.
    /// </summary>
    public void Release(Account? account, WorkflowReservationRequest request)
    {
        Validate(account, request);
        lock (sync)
        {
            if (request.BodyPasses > bodyPasses || request.EvidenceEvents > evidenceEvents ||
                request.DispatchTickets > dispatchTickets ||
                account is not null && (request.Activations > account.ActivationsUsed ||
                    request.ArrayEvidenceBytes > account.ArrayEvidenceBytesUsed))
                throw new InvalidOperationException("A reservation release exceeds what was reserved.");
            Apply(account, request, -1);
        }
    }

    /// <summary>
    /// Reserves and returns a lease that gives the draw back on <see cref="Lease.Dispose"/> unless
    /// <see cref="Lease.Commit"/> was called, so a failure after reservation cannot leak counts.
    /// </summary>
    public Lease? TryAcquire(Account? account, WorkflowReservationRequest request) =>
        TryReserve(account, request) ? new Lease(this, account, request) : null;

    public sealed class Lease : IDisposable
    {
        private readonly WorkflowRunReservation owner;
        private readonly Account? account;
        private readonly WorkflowReservationRequest request;
        private int settled;

        internal Lease(WorkflowRunReservation owner, Account? account, WorkflowReservationRequest request)
        {
            this.owner = owner;
            this.account = account;
            this.request = request;
        }

        /// <summary>Keeps the draw; the counts stay reserved for the life of the run.</summary>
        public void Commit() => Interlocked.Exchange(ref settled, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref settled, 1) == 0) owner.Release(account, request);
        }
    }

    private static void Validate(Account? account, WorkflowReservationRequest request)
    {
        if (request.Activations < 0 || request.BodyPasses < 0 || request.DispatchTickets < 0 ||
            request.EvidenceEvents < 0 || request.ArrayEvidenceBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A reservation cannot draw a negative count.");
        if (account is null && (request.Activations != 0 || request.ArrayEvidenceBytes != 0))
            throw new ArgumentException("Activations and array evidence bytes need an account.", nameof(account));
    }

    private WorkflowReservationOutcome Check(Account? account, WorkflowReservationRequest request)
    {
        if (account is not null && !request.ActivationsExemptFromAccountCap &&
            request.Activations > 0 && account.ActivationsUsed + request.Activations > account.MaximumActivations)
            return WorkflowReservationOutcome.ActivationsExhausted;
        if ((long)bodyPasses + request.BodyPasses > maximumBodyPasses)
            return WorkflowReservationOutcome.BodyPassesExhausted;
        if (maximumDispatchTickets is { } ticketCap && (long)dispatchTickets + request.DispatchTickets > ticketCap)
            return WorkflowReservationOutcome.DispatchTicketsExhausted;
        if ((long)evidenceEvents + request.EvidenceEvents > maximumEvidenceEvents)
            return WorkflowReservationOutcome.EvidenceEventsExhausted;
        if (account is not null && request.ArrayEvidenceBytes > account.MaximumArrayEvidenceBytes - account.ArrayEvidenceBytesUsed)
            return WorkflowReservationOutcome.ArrayEvidenceExhausted;
        return WorkflowReservationOutcome.Reserved;
    }

    private void Apply(Account? account, WorkflowReservationRequest request, int sign)
    {
        bodyPasses += sign * request.BodyPasses;
        evidenceEvents += sign * request.EvidenceEvents;
        dispatchTickets += sign * request.DispatchTickets;
        if (dispatchTickets > dispatchTicketsPeak) dispatchTicketsPeak = dispatchTickets;
        if (account is null) return;
        account.ActivationsUsed += sign * request.Activations;
        account.ArrayEvidenceBytesUsed += sign * request.ArrayEvidenceBytes;
    }
}
