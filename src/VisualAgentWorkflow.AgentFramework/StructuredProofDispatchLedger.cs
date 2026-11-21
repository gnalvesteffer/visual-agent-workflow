using System.Collections.Immutable;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Proof-only accounting for a scheduler that owns every delivery. It is not used by
/// public Agent Framework execution; callers must reserve successor identities before
/// settling the current delivery.
/// </summary>
internal sealed class StructuredProofDispatchLedger(int maximumOutstandingDeliveries = 4096)
{
    internal sealed record BranchScope(Guid ActivationId, ImmutableArray<string> ExpectedBranches, string BranchId);

    internal sealed record Delivery(Guid Id, ImmutableArray<BranchScope> Lineage);

    internal sealed record Successor(ImmutableArray<BranchScope> Lineage);

    internal sealed record JoinArrival(Guid ActivationId, string BranchId, Guid DeliveryId);

    private sealed class ActivationState(ImmutableHashSet<string> expectedBranches,
        ImmutableArray<BranchScope> parentLineage)
    {
        public ImmutableHashSet<string> ExpectedBranches { get; } = expectedBranches;
        public ImmutableArray<BranchScope> ParentLineage { get; } = parentLineage;
        public Dictionary<string, int> ActiveByBranch { get; } = new(StringComparer.Ordinal);
        public List<JoinArrival> Arrivals { get; } = [];
        public TaskCompletionSource<ImmutableArray<JoinArrival>> Quiesced { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Settled { get; set; }
        public bool Failed { get; set; }
        public bool JoinBatchClaimed { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<Guid, Delivery> active = [];
    private readonly Dictionary<Guid, ActivationState> activations = [];
    private readonly int limit = maximumOutstandingDeliveries > 0
        ? maximumOutstandingDeliveries
        : throw new ArgumentOutOfRangeException(nameof(maximumOutstandingDeliveries));

    public int OutstandingDeliveryCount { get { lock (sync) return active.Count; } }

    public Delivery CreateRoot()
    {
        lock (sync)
        {
            var root = new Delivery(Guid.NewGuid(), []);
            active.Add(root.Id, root);
            return root;
        }
    }

    /// <summary>Admits child-workflow root work while retaining the parent call delivery.</summary>
    public Delivery AdmitChildDelivery(Delivery parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        lock (sync)
        {
            EnsureActive(parent);
            EnsureCapacity(1);
            var child = new Delivery(Guid.NewGuid(), parent.Lineage);
            active.Add(child.Id, child);
            IncrementLineage(child, 1);
            return child;
        }
    }

    public (Guid ActivationId, ImmutableArray<Delivery> Deliveries) Fork(Delivery current,
        IEnumerable<string> branchIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(branchIds);
        var branches = branchIds.ToImmutableArray();
        if (branches.Length < 2 || branches.Any(string.IsNullOrWhiteSpace) ||
            branches.Distinct(StringComparer.Ordinal).Count() != branches.Length)
            throw new ArgumentException("A fork requires at least two unique branch IDs.", nameof(branchIds));

        lock (sync)
        {
            EnsureActive(current);
            EnsureCapacity(branches.Length - 1);
            var activationId = Guid.NewGuid();
            var expected = branches.ToImmutableHashSet(StringComparer.Ordinal);
            var state = new ActivationState(expected, current.Lineage);
            activations.Add(activationId, state);
            var children = ImmutableArray.CreateBuilder<Delivery>(branches.Length);
            foreach (var branchId in branches)
            {
                var lineage = current.Lineage.Add(new(activationId, branches, branchId));
                var child = new Delivery(Guid.NewGuid(), lineage);
                active.Add(child.Id, child);
                IncrementLineage(child, 1);
                children.Add(child);
            }
            Settle(current);
            return (activationId, children.MoveToImmutable());
        }
    }

    /// <summary>Atomically reserves all successors before settling the current work.</summary>
    public ImmutableArray<Delivery> Transition(Delivery current, IEnumerable<Successor> successors)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(successors);
        var next = successors.ToArray();
        lock (sync)
        {
            EnsureActive(current);
            EnsureCapacity(next.Length - 1);
            var deliveries = ImmutableArray.CreateBuilder<Delivery>(next.Length);
            foreach (var successor in next)
            {
                var child = new Delivery(Guid.NewGuid(), successor.Lineage);
                active.Add(child.Id, child);
                IncrementLineage(child, 1);
                deliveries.Add(child);
            }
            Settle(current);
            return deliveries.MoveToImmutable();
        }
    }

    /// <summary>
    /// Stages a Join delivery and settles its current work. The batch is released only
    /// when no admitted descendant delivery remains active in any expected branch.
    /// </summary>
    public Task<ImmutableArray<JoinArrival>> StageJoinArrival(Delivery delivery, Guid activationId,
        string branchId, ImmutableArray<string> expectedBranches)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        lock (sync)
        {
            EnsureActive(delivery);
            var expected = expectedBranches.ToImmutableHashSet(StringComparer.Ordinal);
            if (expected.Count < 2 || !expected.Contains(branchId) || delivery.Lineage.IsEmpty ||
                delivery.Lineage[^1] is not { } lineage || lineage.ActivationId != activationId ||
                lineage.BranchId != branchId || !lineage.ExpectedBranches.ToImmutableHashSet(StringComparer.Ordinal)
                    .SetEquals(expected))
                throw new InvalidOperationException("Join arrival does not match its admitted activation lineage.");
            if (!activations.TryGetValue(activationId, out var state) || !state.ExpectedBranches.SetEquals(expected))
                throw new InvalidOperationException("Join activation is stale or has a different branch contract.");
            if (state.Settled) throw new InvalidOperationException("Join activation has already settled.");
            if (state.Arrivals.Count >= limit)
                throw new InvalidOperationException("Structured proof Join-arrival limit exceeded.");

            state.Arrivals.Add(new(activationId, branchId, delivery.Id));
            Settle(delivery);
            if (state.ActiveByBranch.Values.All(count => count == 0))
            {
                state.Settled = true;
                state.Quiesced.TrySetResult(state.Arrivals.ToImmutableArray());
            }
            return state.Quiesced.Task;
        }
    }

    public Delivery AdmitAdditionalDelivery(Delivery admittedDelivery)
    {
        ArgumentNullException.ThrowIfNull(admittedDelivery);
        lock (sync)
        {
            EnsureActive(admittedDelivery);
            EnsureCapacity(1);
            var duplicate = new Delivery(Guid.NewGuid(), admittedDelivery.Lineage);
            active.Add(duplicate.Id, duplicate);
            IncrementLineage(duplicate, 1);
            return duplicate;
        }
    }

    public bool TryClaimJoinBatch(Guid activationId)
    {
        lock (sync)
        {
            if (!activations.TryGetValue(activationId, out var state) || !state.Settled ||
                !state.Quiesced.Task.IsCompletedSuccessfully || state.JoinBatchClaimed)
                return false;
            state.JoinBatchClaimed = true;
            return true;
        }
    }

    public void FailAll(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (sync)
        {
            foreach (var activationId in activations.Keys.ToArray())
                FailActivation(activationId, error);
            foreach (var delivery in active.Values.ToArray()) Settle(delivery);
        }
    }

    public void FailActivation(Guid activationId, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (sync)
        {
            if (!activations.TryGetValue(activationId, out var state) || state.Failed) return;
            var affectedActivations = activations.Where(pair => pair.Key == activationId ||
                pair.Value.ParentLineage.Any(scope => scope.ActivationId == activationId))
                .Select(pair => pair.Key).ToHashSet();
            foreach (var delivery in active.Values.Where(item =>
                         item.Lineage.Any(scope => affectedActivations.Contains(scope.ActivationId))).ToArray())
                Settle(delivery);
            foreach (var affectedId in affectedActivations)
            {
                var affected = activations[affectedId];
                affected.Settled = true;
                affected.Failed = true;
                affected.Arrivals.Clear();
                affected.Quiesced.TrySetException(error);
                activations.Remove(affectedId);
            }
        }
    }

    public void CleanupActivation(Guid activationId)
    {
        lock (sync)
        {
            if (!activations.TryGetValue(activationId, out var state)) return;
            if (state.ActiveByBranch.Values.Any(count => count != 0))
                throw new InvalidOperationException("Cannot clean up an activation with outstanding work.");
            activations.Remove(activationId);
        }
    }

    /// <summary>Reserves a Join continuation in the parent lineage after quiescence.</summary>
    public Delivery CollapseJoin(Guid activationId)
    {
        lock (sync)
        {
            if (!activations.TryGetValue(activationId, out var state) || !state.Settled ||
                !state.Quiesced.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Join cannot collapse before successful quiescence.");
            EnsureCapacity(1);
            var continuation = new Delivery(Guid.NewGuid(), state.ParentLineage);
            active.Add(continuation.Id, continuation);
            IncrementLineage(continuation, 1);
            activations.Remove(activationId);
            return continuation;
        }
    }

    private void EnsureActive(Delivery delivery)
    {
        if (!active.TryGetValue(delivery.Id, out var stored) || stored != delivery)
            throw new InvalidOperationException("Delivery is stale, already settled, or was not admitted.");
    }

    private void EnsureCapacity(int delta)
    {
        if (delta > 0 && active.Count + delta > limit)
            throw new InvalidOperationException("Structured proof delivery limit exceeded.");
    }

    private void IncrementLineage(Delivery delivery, int delta)
    {
        foreach (var scope in delivery.Lineage)
        {
            if (!activations.TryGetValue(scope.ActivationId, out var state) || state.Settled)
                throw new InvalidOperationException("Delivery contains stale activation lineage.");
            state.ActiveByBranch.TryGetValue(scope.BranchId, out var count);
            state.ActiveByBranch[scope.BranchId] = count + delta;
        }
    }

    private void Settle(Delivery delivery)
    {
        if (!active.Remove(delivery.Id))
            throw new InvalidOperationException("Delivery was settled more than once.");
        IncrementLineage(delivery, -1);
    }
}
