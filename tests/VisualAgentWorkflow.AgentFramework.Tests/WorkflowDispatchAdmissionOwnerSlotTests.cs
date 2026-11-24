using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Owner-only unit proofs for multi-receiver admission tickets (Decision 0070 review, 2026-11-23).</summary>
public sealed class WorkflowDispatchAdmissionOwnerSlotTests
{
    [Fact]
    public void Multi_receiver_ticket_admits_every_receiver_once_and_settles_to_quiescence()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["output", "sequence-collector"]);

        owner.Begin(id, "output");
        owner.Begin(id, "sequence-collector");
        owner.Settle(id, "sequence-collector");
        owner.Settle(id, "output");

        owner.EnsureQuiescent();
    }

    [Fact]
    public void Observer_sees_one_admission_per_receiver_slot_with_the_shared_ticket_id()
    {
        var seen = new List<WorkflowDispatchAdmissionOwner.Ticket>();
        var owner = new WorkflowDispatchAdmissionOwner(seen.Add);

        var id = owner.Admit(["a", "b"]);

        Assert.Equal(["a", "b"], seen.Select(ticket => ticket.TargetNodeId));
        Assert.All(seen, ticket => Assert.Equal(id, ticket.Id));
    }

    [Fact]
    public void Repeated_begin_for_one_receiver_is_rejected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["a", "b"]);
        owner.Begin(id, "a");

        Assert.Throws<InvalidOperationException>(() => owner.Begin(id, "a"));
    }

    [Fact]
    public void Begin_from_a_receiver_outside_the_ticket_is_rejected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["a", "b"]);

        Assert.Throws<InvalidOperationException>(() => owner.Begin(id, "c"));
    }

    [Fact]
    public void Begin_with_a_missing_or_unknown_ticket_is_rejected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();

        Assert.Throws<InvalidOperationException>(() => owner.Begin(null, "a"));
        Assert.Throws<InvalidOperationException>(() => owner.Begin(Guid.NewGuid(), "a"));
    }

    [Fact]
    public void Settle_without_a_started_slot_is_rejected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["a", "b"]);
        owner.Begin(id, "a");

        Assert.Throws<InvalidOperationException>(() => owner.Settle(id, "b"));
    }

    [Fact]
    public void Quiescence_fails_while_any_slot_is_unsettled()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["a", "b"]);
        owner.Begin(id, "a");
        owner.Settle(id, "a");

        Assert.Throws<InvalidOperationException>(owner.EnsureQuiescent);
        owner.Begin(id, "b");
        Assert.Throws<InvalidOperationException>(owner.EnsureQuiescent);
        owner.Settle(id, "b");
        owner.EnsureQuiescent();
    }

    [Fact]
    public void Revoke_is_allowed_only_while_every_slot_is_still_admitted()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var unsent = owner.Admit(["a", "b"]);
        owner.Revoke(unsent);
        owner.EnsureQuiescent();

        var partlyStarted = owner.Admit(["a", "b"]);
        owner.Begin(partlyStarted, "a");
        Assert.Throws<InvalidOperationException>(() => owner.Revoke(partlyStarted));
    }

    [Fact]
    public void Single_target_admission_keeps_its_original_behavior()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit("a");

        owner.Begin(id, "a");
        Assert.Throws<InvalidOperationException>(() => owner.Begin(id, "a"));
        owner.Settle(id, "a");
        owner.EnsureQuiescent();
    }

    [Fact]
    public void Duplicate_targets_collapse_to_one_slot_and_an_empty_set_is_rejected()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        var id = owner.Admit(["a", "a"]);
        owner.Begin(id, "a");
        owner.Settle(id, "a");
        owner.EnsureQuiescent();

        Assert.Throws<ArgumentException>(() => owner.Admit(Array.Empty<string>()));
    }

    [Fact]
    public void Outstanding_reports_every_unsettled_slot_with_its_state_until_quiescent()
    {
        var owner = new WorkflowDispatchAdmissionOwner();
        Assert.Empty(owner.Outstanding());

        var id = owner.Admit(["a", "b"]);
        Assert.Equal(["a:Admitted", "b:Admitted"], owner.Outstanding());

        owner.Begin(id, "a");
        Assert.Equal(["a:Started", "b:Admitted"], owner.Outstanding());

        owner.Begin(id, "b");
        owner.Settle(id, "a");
        owner.Settle(id, "b");
        Assert.Empty(owner.Outstanding());
    }

    [Fact]
    public void The_probe_receives_the_owner_and_the_observer_is_exposed_for_child_runs()
    {
        WorkflowDispatchAdmissionOwner? probed = null;
        Action<WorkflowDispatchAdmissionOwner.Ticket> observer = _ => { };

        var owner = new WorkflowDispatchAdmissionOwner(observer, created => probed = created);

        Assert.Same(owner, probed);
        Assert.Same(observer, owner.Observer);
        Assert.NotNull(owner.Probe);
    }

    [Fact]
    public void A_throwing_observer_leaves_no_slots_behind()
    {
        var owner = new WorkflowDispatchAdmissionOwner(ticket =>
        {
            if (ticket.TargetNodeId == "b") throw new InvalidOperationException("rejected");
        });

        Assert.Throws<InvalidOperationException>(() => owner.Admit(["a", "b"]));
        owner.EnsureQuiescent();
    }
}
