using System.Collections.Concurrent;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowRunReservationTests
{
    [Fact]
    public void Concurrent_reservers_never_exceed_any_cap_and_refusals_leave_no_partial_draw()
    {
        var reservation = new WorkflowRunReservation(maximumBodyPasses: 40, maximumEvidenceEvents: 1000, maximumDispatchTickets: 25);
        var accounts = Enumerable.Range(0, 4).Select(_ => reservation.CreateAccount(15, 100)).ToArray();
        var granted = new ConcurrentBag<(int Account, int Activations)>();
        var maximumObservedPasses = 0;
        var maximumObservedTickets = 0;

        Parallel.For(0, 4_000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, index =>
        {
            var accountIndex = index % accounts.Length;
            var request = new WorkflowReservationRequest(Activations: 1, BodyPasses: 1, DispatchTickets: 1,
                EvidenceEvents: 2, ArrayEvidenceBytes: 7);
            if (reservation.TryReserve(accounts[accountIndex], request))
                granted.Add((accountIndex, 1));
            InterlockedMax(ref maximumObservedPasses, reservation.BodyPassesUsed);
            InterlockedMax(ref maximumObservedTickets, reservation.DispatchTicketsOutstanding);
        });

        // Tickets (25) are the tightest run-wide cap: exactly 25 whole draws succeed, no more.
        Assert.Equal(25, granted.Count);
        Assert.Equal(25, reservation.BodyPassesUsed);
        Assert.Equal(25, reservation.DispatchTicketsOutstanding);
        Assert.Equal(50, reservation.EvidenceEventsUsed);
        Assert.InRange(maximumObservedPasses, 1, 40);
        Assert.InRange(maximumObservedTickets, 1, 25);
        foreach (var (account, index) in accounts.Select((account, index) => (account, index)))
        {
            var count = granted.Count(entry => entry.Account == index);
            Assert.Equal(count, reservation.ActivationsUsed(account));
            Assert.Equal(count * 7, reservation.ArrayEvidenceBytesUsed(account));
            Assert.InRange(count, 0, 14); // 100 bytes / 7 per draw is the tighter account cap
        }
    }

    [Fact]
    public void Each_pool_refuses_at_its_own_cap_and_names_the_first_refusing_pool()
    {
        var reservation = new WorkflowRunReservation(maximumBodyPasses: 2, maximumEvidenceEvents: 3, maximumDispatchTickets: 1);
        var account = reservation.CreateAccount(1, 10);

        Assert.True(reservation.TryReserve(account, new(Activations: 1), out _));
        Assert.False(reservation.TryReserve(account, new(Activations: 1, BodyPasses: 1), out var activations));
        Assert.Equal(WorkflowReservationOutcome.ActivationsExhausted, activations);
        Assert.False(reservation.TryReserve(null, new(BodyPasses: 3), out var passes));
        Assert.Equal(WorkflowReservationOutcome.BodyPassesExhausted, passes);
        Assert.True(reservation.TryReserve(null, new(DispatchTickets: 1)));
        Assert.False(reservation.TryReserve(null, new(DispatchTickets: 1), out var tickets));
        Assert.Equal(WorkflowReservationOutcome.DispatchTicketsExhausted, tickets);
        Assert.False(reservation.TryReserve(null, new(EvidenceEvents: 4), out var events));
        Assert.Equal(WorkflowReservationOutcome.EvidenceEventsExhausted, events);
        Assert.False(reservation.TryReserve(account, new(ArrayEvidenceBytes: 11), out var bytes));
        Assert.Equal(WorkflowReservationOutcome.ArrayEvidenceExhausted, bytes);
        Assert.Equal(0, reservation.BodyPassesUsed);
        Assert.Equal(0, reservation.EvidenceEventsUsed);
        Assert.Equal(0, reservation.ArrayEvidenceBytesUsed(account));
    }

    [Fact]
    public void Release_and_disposed_leases_return_every_count_so_nothing_leaks()
    {
        var reservation = new WorkflowRunReservation(maximumBodyPasses: 4, maximumDispatchTickets: 4);
        var account = reservation.CreateAccount(4, 64);
        var request = new WorkflowReservationRequest(Activations: 1, BodyPasses: 1, DispatchTickets: 1, ArrayEvidenceBytes: 8);

        Parallel.For(0, 2_000, _ =>
        {
            using var lease = reservation.TryAcquire(account, request); // dropped without Commit: always returned
        });
        Assert.Equal(0, reservation.BodyPassesUsed);
        Assert.Equal(0, reservation.DispatchTicketsOutstanding);
        Assert.Equal(0, reservation.ActivationsUsed(account));
        Assert.Equal(0, reservation.ArrayEvidenceBytesUsed(account));

        var lease = reservation.TryAcquire(account, request)!;
        lease.Commit();
        lease.Dispose();
        Assert.Equal(1, reservation.BodyPassesUsed);

        reservation.Release(account, request);
        Assert.Equal(0, reservation.BodyPassesUsed);
        Assert.Throws<InvalidOperationException>(() => reservation.Release(account, request));
        Assert.Equal(0, reservation.BodyPassesUsed);
    }

    [Fact]
    public void Array_evidence_budget_on_an_account_keeps_the_existing_limit_and_message()
    {
        var reservation = new WorkflowRunReservation();
        var account = reservation.CreateAccount(1, WorkflowArrayEvidenceBudget.MaximumAggregateBytes);
        var budget = new WorkflowArrayEvidenceBudget(reservation, account);
        var plain = new WorkflowArrayEvidenceBudget();
        var chunk = "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 998) + "\"", 15)) + "]";
        string? shared = null, standalone = null;
        for (var i = 0; i < 10 && (shared is null || standalone is null); i++)
        {
            try { budget.Consume("array<string>", chunk); } catch (WorkflowArrayEvidenceBudgetExceededException e) { shared = e.Message; }
            try { plain.Consume("array<string>", chunk); } catch (WorkflowArrayEvidenceBudgetExceededException e) { standalone = e.Message; }
        }
        Assert.NotNull(shared);
        Assert.Equal(standalone, shared);
        Assert.Equal(plain.UsedBytes, budget.UsedBytes);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
            Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
