using VisualAgentWorkflow.AgentFramework;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class StructuredProofDispatchLedgerTests
{
    [Fact]
    public async Task JoinWaitsForEveryAdmittedDuplicateBeforeReleasingBatch()
    {
        var ledger = new StructuredProofDispatchLedger();
        var root = ledger.CreateRoot();
        var (activation, branches) = ledger.Fork(root, ["a", "b"]);
        var duplicateDeliveries = ledger.Transition(branches[0], [
            new(branches[0].Lineage), new(branches[0].Lineage)
        ]);
        var firstA = ledger.StageJoinArrival(duplicateDeliveries[0], activation, "a", ["a", "b"]);
        Assert.False(firstA.IsCompleted);

        var finalB = ledger.StageJoinArrival(branches[1], activation, "b", ["a", "b"]);
        Assert.False(finalB.IsCompleted);
        var duplicateA = ledger.StageJoinArrival(duplicateDeliveries[1], activation, "a", ["a", "b"]);
        var batch = await duplicateA.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "a", "b", "a" }, batch.Select(arrival => arrival.BranchId));
        Assert.Same(firstA, finalB);
    }

    [Fact]
    public async Task UniqueArrivalsReleaseAndCollapseExactlyOnce()
    {
        var ledger = new StructuredProofDispatchLedger();
        var (activation, branches) = ledger.Fork(ledger.CreateRoot(), ["a", "b"]);
        var a = ledger.StageJoinArrival(branches[0], activation, "a", ["a", "b"]);
        var b = ledger.StageJoinArrival(branches[1], activation, "b", ["a", "b"]);

        var batch = await b.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, batch.Length);
        Assert.Same(a, b);
        var continuation = ledger.CollapseJoin(activation);

        Assert.Empty(continuation.Lineage);
        Assert.Throws<InvalidOperationException>(() => ledger.CollapseJoin(activation));
    }

    [Fact]
    public async Task NestedJoinCollapseRestoresParentLineageAndUnrelatedWorkDoesNotDelayIt()
    {
        var ledger = new StructuredProofDispatchLedger();
        var (outer, outerBranches) = ledger.Fork(ledger.CreateRoot(), ["left", "right"]);
        var (inner, innerBranches) = ledger.Fork(outerBranches[0], ["one", "two"]);
        var unrelated = ledger.Fork(ledger.CreateRoot(), ["x", "y"]);
        var firstInner = ledger.StageJoinArrival(innerBranches[0], inner, "one", ["one", "two"]);
        var innerComplete = ledger.StageJoinArrival(innerBranches[1], inner, "two", ["one", "two"]);
        await innerComplete.WaitAsync(TimeSpan.FromSeconds(1));
        var innerContinuation = ledger.CollapseJoin(inner);

        Assert.Single(innerContinuation.Lineage);
        Assert.Equal(outer, innerContinuation.Lineage[0].ActivationId);
        Assert.False(firstInner.IsFaulted);
        Assert.False(firstInner.IsCanceled);
        Assert.False(ledger.StageJoinArrival(outerBranches[1], outer, "right", ["left", "right"]).IsCompleted);
        var outerLeft = ledger.StageJoinArrival(innerContinuation, outer, "left", ["left", "right"]);
        Assert.True(outerLeft.IsCompletedSuccessfully);
        Assert.Equal(2, (await outerLeft).Length);
        Assert.Equal(2, unrelated.Deliveries.Length);
    }

    [Fact]
    public async Task FailureSettlesDescendantsAndRejectsStaleOrDuplicateDelivery()
    {
        var ledger = new StructuredProofDispatchLedger();
        var (outer, branches) = ledger.Fork(ledger.CreateRoot(), ["a", "b"]);
        var (inner, nested) = ledger.Fork(branches[0], ["x", "y"]);
        var pending = ledger.StageJoinArrival(branches[1], outer, "b", ["a", "b"]);
        var failure = new InvalidOperationException("branch failed");

        ledger.FailActivation(outer, failure);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Same(failure, observed);
        Assert.Equal(0, ledger.OutstandingDeliveryCount);
        Assert.Throws<InvalidOperationException>(() => ledger.Transition(nested[0], []));
        Assert.Throws<InvalidOperationException>(() => ledger.Transition(branches[0], []));
        Assert.Throws<InvalidOperationException>(() => ledger.CollapseJoin(inner));
    }

    [Fact]
    public async Task AdmissionLimitAndMalformedLineageFailClosed()
    {
        var ledger = new StructuredProofDispatchLedger(maximumOutstandingDeliveries: 2);
        var root = ledger.CreateRoot();
        var (activation, branches) = ledger.Fork(root, ["a", "b"]);

        Assert.Throws<InvalidOperationException>(() => ledger.Transition(branches[0], [
            new(branches[0].Lineage), new(branches[0].Lineage)
        ]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.StageJoinArrival(branches[0], Guid.NewGuid(),
            "a", ["a", "b"]));
        var tampered = branches[0] with { Lineage = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.StageJoinArrival(tampered, activation,
            "a", ["a", "b"]));

        var transition = ledger.Transition(branches[0], []);
        Assert.Empty(transition);
        Assert.Throws<InvalidOperationException>(() => ledger.Transition(branches[0], []));
        ledger.FailActivation(activation, new OperationCanceledException("cancelled"));

        var arrivalLedger = new StructuredProofDispatchLedger(maximumOutstandingDeliveries: 2);
        var (arrivalActivation, arrivalBranches) = arrivalLedger.Fork(arrivalLedger.CreateRoot(), ["a", "b"]);
        var pending = arrivalLedger.StageJoinArrival(arrivalBranches[1], arrivalActivation, "b", ["a", "b"]);
        var duplicate = arrivalLedger.AdmitAdditionalDelivery(arrivalBranches[0]);
        _ = arrivalLedger.StageJoinArrival(arrivalBranches[0], arrivalActivation, "a", ["a", "b"]);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = arrivalLedger.StageJoinArrival(duplicate, arrivalActivation, "a", ["a", "b"]);
        });
        arrivalLedger.FailActivation(arrivalActivation, new OperationCanceledException("arrival bound"));
        Assert.Equal(0, arrivalLedger.OutstandingDeliveryCount);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }
}
