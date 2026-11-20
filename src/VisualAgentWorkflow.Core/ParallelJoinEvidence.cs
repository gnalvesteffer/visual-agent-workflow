using System.Collections.Immutable;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>One active fork identity in an accepted nested parallel arrival's root-to-leaf lineage.</summary>
public sealed record ParallelForkActivationIdentity(string ForkNodeId, Guid ActivationId, string BranchId,
    string? ParentForkNodeId, Guid? ParentActivationId, string? ParentBranchId);

/// <summary>Versioned evidence that a unified Fork activation was admitted before branch dispatch.</summary>
public sealed record ParallelJoinActivationEvidence(int Version, string JoinNodeId, string ForkNodeId,
    Guid ForkActivationId, ImmutableArray<string> ExpectedBranchIds,
    ImmutableArray<ParallelForkActivationIdentity> ParentLineage)
{
    public const int CurrentVersion = 1;

    public string Serialize() => JsonSerializer.Serialize(this);

    public static bool TryRead(ExecutionEvent executionEvent, out ParallelJoinActivationEvidence? evidence)
    {
        evidence = null;
        if (executionEvent.Kind != ExecutionEventKinds.ParallelJoinActivated ||
            string.IsNullOrWhiteSpace(executionEvent.Data) || executionEvent.Data.Length > 64 * 1024) return false;

        try
        {
            evidence = JsonSerializer.Deserialize<ParallelJoinActivationEvidence>(executionEvent.Data);
            return evidence is not null && evidence.Version == CurrentVersion &&
                !string.IsNullOrWhiteSpace(evidence.JoinNodeId) && !string.IsNullOrWhiteSpace(evidence.ForkNodeId) &&
                evidence.ForkActivationId != Guid.Empty && evidence.ExpectedBranchIds is { IsDefault: false } branches &&
                branches.Length is >= ParallelBranchLimits.MinimumBranches and <= ParallelGraphLimits.MaximumExplicitBranches &&
                branches.All(branch => !string.IsNullOrWhiteSpace(branch)) &&
                branches.Distinct(StringComparer.Ordinal).Count() == branches.Length &&
                evidence.ParentLineage is { IsDefault: false } lineage && lineage.Length <= 64 &&
                executionEvent.NodeId == evidence.ForkNodeId && executionEvent.InvocationId == evidence.ForkActivationId &&
                IsValidParentLineage(lineage);
        }
        catch (JsonException)
        {
            evidence = null;
            return false;
        }
    }

    internal static bool IsValidParentLineage(ImmutableArray<ParallelForkActivationIdentity> lineage)
    {
        var seenForks = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lineage.Length; index++)
        {
            var item = lineage[index];
            if (item is null || string.IsNullOrWhiteSpace(item.ForkNodeId) || item.ActivationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.BranchId) || !seenForks.Add(item.ForkNodeId)) return false;
            if (index == 0)
            {
                if (item.ParentForkNodeId is not null || item.ParentActivationId is not null || item.ParentBranchId is not null)
                    return false;
            }
            else
            {
                var parent = lineage[index - 1];
                if (item.ParentForkNodeId != parent.ForkNodeId || item.ParentActivationId != parent.ActivationId ||
                    item.ParentBranchId != parent.BranchId) return false;
            }
        }
        return true;
    }
}

/// <summary>Versioned evidence for a branch result accepted by a schema 2.0 parallel join.</summary>
public sealed record ParallelJoinArrivalEvidence(int Version, string JoinNodeId, string ForkNodeId,
    Guid ForkActivationId, string BranchId, ImmutableArray<ParallelForkActivationIdentity> Lineage)
{
    public const int CurrentVersion = 1;

    public string Serialize() => JsonSerializer.Serialize(this);

    public static bool TryRead(ExecutionEvent executionEvent, out ParallelJoinArrivalEvidence? evidence)
    {
        evidence = null;
        if (executionEvent.Kind != ExecutionEventKinds.ParallelJoinArrival ||
            string.IsNullOrWhiteSpace(executionEvent.Data) || executionEvent.Data.Length > 64 * 1024) return false;

        try
        {
            evidence = JsonSerializer.Deserialize<ParallelJoinArrivalEvidence>(executionEvent.Data);
            return evidence is not null && evidence.Version == CurrentVersion &&
                !string.IsNullOrWhiteSpace(evidence.JoinNodeId) && !string.IsNullOrWhiteSpace(evidence.ForkNodeId) &&
                evidence.ForkActivationId != Guid.Empty && !string.IsNullOrWhiteSpace(evidence.BranchId) &&
                !evidence.Lineage.IsDefaultOrEmpty && evidence.Lineage.Length <= 64 &&
                executionEvent.NodeId == evidence.JoinNodeId && executionEvent.InvocationId == evidence.ForkActivationId &&
                IsValidLineage(evidence);
        }
        catch (JsonException)
        {
            evidence = null;
            return false;
        }
    }

    private static bool IsValidLineage(ParallelJoinArrivalEvidence evidence)
    {
        var seenForks = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < evidence.Lineage.Length; index++)
        {
            var item = evidence.Lineage[index];
            if (item is null || string.IsNullOrWhiteSpace(item.ForkNodeId) || item.ActivationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.BranchId) || !seenForks.Add(item.ForkNodeId)) return false;
            if (index == 0)
            {
                if (item.ParentForkNodeId is not null || item.ParentActivationId is not null || item.ParentBranchId is not null)
                    return false;
            }
            else
            {
                var parent = evidence.Lineage[index - 1];
                if (item.ParentForkNodeId != parent.ForkNodeId || item.ParentActivationId != parent.ActivationId ||
                    item.ParentBranchId != parent.BranchId) return false;
            }
        }

        var leaf = evidence.Lineage[^1];
        return leaf.ForkNodeId == evidence.ForkNodeId && leaf.ActivationId == evidence.ForkActivationId &&
            leaf.BranchId == evidence.BranchId;
    }
}

public sealed record ParallelJoinPlaybackActivation(string JoinNodeId, Guid ForkActivationId, string State,
    ImmutableArray<string> ReceivedBranchIds, ImmutableArray<string> ExpectedBranchIds,
    ImmutableArray<ImmutableArray<ParallelForkActivationIdentity>> Lineages, long FirstSequence)
{
    public ImmutableArray<ParallelForkActivationIdentity> ActivationLineage { get; init; } = [];
}

/// <summary>Passive root-graph projection of accepted parallel arrivals; scoped child histories need their own definition.</summary>
public sealed record ParallelJoinPlaybackSnapshot(string Status,
    ImmutableArray<ParallelJoinPlaybackActivation> Activations);

public static class ParallelJoinEvidence
{
    /// <summary>Validates and reconstructs the supplied graph's unscoped Join-arrival history without execution.</summary>
    public static bool TryReconstructPlayback(WorkflowDefinition definition,
        IEnumerable<ExecutionEvent> executionEvents, out ParallelJoinPlaybackSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(executionEvents);
        snapshot = null;
        if (definition.SchemaVersion != WorkflowSchemaVersions.UnifiedWorkflowGraph ||
            !WorkflowValidator.Validate(definition).IsEmpty) return false;
        if (!TryBuildNestedForkOwners(definition, out var nestedForkOwners)) return false;

        var events = executionEvents.OrderBy(evt => evt.Sequence).ToArray();
        var hasActivationEvents = events.Any(evt => evt.Kind == ExecutionEventKinds.ParallelJoinActivated);
        Guid? runId = null;
        long priorSequence = 0;
        var activations = new Dictionary<(string Scope, string Join, Guid ForkActivation), MutableActivation>();
        string? runState = null;
        foreach (var evt in events)
        {
            if (runState is not null) return false;
            if (evt.Sequence <= priorSequence || (runId is { } expectedRunId && evt.RunId != expectedRunId)) return false;
            runId ??= evt.RunId;
            priorSequence = evt.Sequence;
            if (evt.ScopeId is not null) continue;
            if (evt.Kind == ExecutionEventKinds.ParallelJoinActivated)
            {
                if (!ParallelJoinActivationEvidence.TryRead(evt, out var evidence) || evidence is null ||
                    !ValidateAgainstDefinition(definition, nestedForkOwners, evidence)) return false;
                var key = (string.Empty, evidence.JoinNodeId, evidence.ForkActivationId);
                if (activations.ContainsKey(key)) return false;
                activations.Add(key, new(key.Item1, evidence.JoinNodeId, evidence.ForkActivationId, evt.Sequence,
                    evidence.ExpectedBranchIds, evidence.ParentLineage));
                continue;
            }
            if (evt.Kind == ExecutionEventKinds.ParallelJoinArrival)
            {
                if (runState is not null || !ParallelJoinArrivalEvidence.TryRead(evt, out var evidence) || evidence is null ||
                    !ValidateAgainstDefinition(definition, nestedForkOwners, evidence)) return false;

                var key = (evt.ScopeId ?? string.Empty, evidence.JoinNodeId, evidence.ForkActivationId);
                if (!activations.TryGetValue(key, out var activation) && hasActivationEvents) return false;
                if (activation is null)
                {
                    var lineage = evidence.Lineage;
                    activation = new(key.Item1, evidence.JoinNodeId, evidence.ForkActivationId, evt.Sequence,
                        definition.Nodes[evidence.JoinNodeId].ParallelJoin!.BranchIds, lineage.RemoveAt(lineage.Length - 1));
                    activations.Add(key, activation);
                }
                var leaf = evidence.Lineage[^1];
                if (!activation.Expected.Contains(evidence.BranchId, StringComparer.Ordinal) ||
                    !activation.ParentLineage.Add(leaf).SequenceEqual(evidence.Lineage) ||
                    !activation.Arrivals.TryAdd(evidence.BranchId, evidence.Lineage)) return false;
                continue;
            }

            if (evt.Kind is (ExecutionEventKinds.Completed or ExecutionEventKinds.Terminated or
                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted or ExecutionEventKinds.OutcomeUnknown))
            {
                if (runState is not null) return false;
                runState = evt.Kind switch
                {
                    ExecutionEventKinds.Completed => "completed",
                    ExecutionEventKinds.Terminated => "terminated",
                    ExecutionEventKinds.Failed => "failed",
                    ExecutionEventKinds.Cancelled => "cancelled",
                    ExecutionEventKinds.Interrupted => "interrupted",
                    ExecutionEventKinds.OutcomeUnknown => "outcome-unknown",
                    _ => throw new InvalidOperationException("Unsupported parallel playback terminal.")
                };
                continue;
            }

            if (evt.NodeId is null || evt.InvocationId is null) continue;
            var matches = activations.Values.Where(item => item.ScopeId.Length == 0 && item.JoinNodeId == evt.NodeId &&
                item.ForkActivationId == evt.InvocationId).ToArray();
            if (matches.Length > 1) return false;
            if (matches.Length == 0) continue;
            var matched = matches[0];
            if (evt.Kind is ExecutionEventKinds.NodeCompleted or ExecutionEventKinds.NodeFailed or ExecutionEventKinds.NodeCancelled)
            {
                if (matched.State != "running") return false;
                if (evt.Kind == ExecutionEventKinds.NodeCompleted && matched.Arrivals.Count != matched.Expected.Length)
                    return false;
                matched.State = evt.Kind switch
                {
                    ExecutionEventKinds.NodeCompleted => "completed",
                    ExecutionEventKinds.NodeFailed => "failed",
                    ExecutionEventKinds.NodeCancelled => "cancelled",
                    _ => matched.State
                };
            }
        }

        if (activations.Count == 0) return false;
        if (runState is not null)
            foreach (var activation in activations.Values.Where(item => item.State == "running"))
                activation.State = "incomplete";
        var projected = activations.Values.OrderBy(item => item.FirstSequence)
            .Select(item => new ParallelJoinPlaybackActivation(item.JoinNodeId, item.ForkActivationId, item.State,
                item.Expected.Where(item.Arrivals.ContainsKey).ToImmutableArray(), item.Expected,
                item.Arrivals.Values.ToImmutableArray(), item.FirstSequence) { ActivationLineage = item.ParentLineage }).ToImmutableArray();
        var status = runState ?? (projected.Any(item => item.State == "failed") ? "failed" :
            projected.Any(item => item.State == "cancelled") ? "cancelled" :
            projected.All(item => item.State == "completed") ? "completed" : "incomplete");
        snapshot = new(status, projected);
        return true;
    }

    private static bool ValidateAgainstDefinition(WorkflowDefinition definition,
        IReadOnlyDictionary<string, (string ForkNodeId, string BranchId)> nestedForkOwners,
        ParallelJoinArrivalEvidence evidence)
    {
        if (!definition.Nodes.TryGetValue(evidence.JoinNodeId, out var join) ||
            join?.Type != NodeTypes.ParallelJoin || join.ParallelJoin?.ForkNodeId != evidence.ForkNodeId ||
            !definition.Nodes.TryGetValue(evidence.ForkNodeId, out var fork) || fork?.Type != NodeTypes.ParallelFork ||
            fork.ParallelFork?.Branches is not { IsDefault: false } branches ||
            !branches.Any(branch => branch.Id == evidence.BranchId) ||
            !join.ParallelJoin.BranchIds.Contains(evidence.BranchId, StringComparer.Ordinal)) return false;

        for (var index = 0; index < evidence.Lineage.Length; index++)
        {
            var identity = evidence.Lineage[index];
            if (!definition.Nodes.TryGetValue(identity.ForkNodeId, out var lineageFork) ||
                lineageFork?.Type != NodeTypes.ParallelFork ||
                lineageFork.ParallelFork?.Branches is not { IsDefault: false } lineageBranches ||
                !lineageBranches.Any(branch => branch.Id == identity.BranchId)) return false;
            if (index == 0)
            {
                if (nestedForkOwners.ContainsKey(identity.ForkNodeId)) return false;
            }
            else if (!nestedForkOwners.TryGetValue(identity.ForkNodeId, out var owner) ||
                owner.ForkNodeId != evidence.Lineage[index - 1].ForkNodeId ||
                owner.BranchId != evidence.Lineage[index - 1].BranchId) return false;
        }
        return true;
    }

    private static bool ValidateAgainstDefinition(WorkflowDefinition definition,
        IReadOnlyDictionary<string, (string ForkNodeId, string BranchId)> nestedForkOwners,
        ParallelJoinActivationEvidence evidence)
    {
        if (!definition.Nodes.TryGetValue(evidence.JoinNodeId, out var join) ||
            join?.Type != NodeTypes.ParallelJoin || join.ParallelJoin?.ForkNodeId != evidence.ForkNodeId ||
            !definition.Nodes.TryGetValue(evidence.ForkNodeId, out var fork) || fork?.Type != NodeTypes.ParallelFork ||
            fork.ParallelFork?.Branches is not { IsDefault: false } forkBranches ||
            !forkBranches.Select(branch => branch.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(evidence.ExpectedBranchIds) ||
            join.ParallelJoin.BranchIds.Length != evidence.ExpectedBranchIds.Length ||
            !join.ParallelJoin.BranchIds.ToHashSet(StringComparer.Ordinal).SetEquals(evidence.ExpectedBranchIds)) return false;

        if (evidence.ParentLineage.IsEmpty) return !nestedForkOwners.ContainsKey(evidence.ForkNodeId);
        if (!nestedForkOwners.TryGetValue(evidence.ForkNodeId, out var owner) ||
            owner.ForkNodeId != evidence.ParentLineage[^1].ForkNodeId ||
            owner.BranchId != evidence.ParentLineage[^1].BranchId) return false;

        for (var index = 0; index < evidence.ParentLineage.Length; index++)
        {
            var identity = evidence.ParentLineage[index];
            if (!definition.Nodes.TryGetValue(identity.ForkNodeId, out var ancestor) || ancestor?.Type != NodeTypes.ParallelFork ||
                ancestor.ParallelFork?.Branches is not { IsDefault: false } ancestorBranches ||
                !ancestorBranches.Any(branch => branch.Id == identity.BranchId)) return false;
            if (index > 0 && (!nestedForkOwners.TryGetValue(identity.ForkNodeId, out var parentOwner) ||
                parentOwner.ForkNodeId != evidence.ParentLineage[index - 1].ForkNodeId ||
                parentOwner.BranchId != evidence.ParentLineage[index - 1].BranchId)) return false;
        }
        return true;
    }

    private static bool TryBuildNestedForkOwners(WorkflowDefinition definition,
        out Dictionary<string, (string ForkNodeId, string BranchId)> owners)
    {
        var ownerMap = new Dictionary<string, (string ForkNodeId, string BranchId)>(StringComparer.Ordinal);
        owners = ownerMap;
        var edges = definition.ExecutionConnections!.Values.ToArray();
        var joins = definition.Nodes.Where(pair => pair.Value.Type == NodeTypes.ParallelJoin)
            .GroupBy(pair => pair.Value.ParallelJoin!.ForkNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().Key, StringComparer.Ordinal);

        foreach (var (forkId, fork) in definition.Nodes.Where(pair => pair.Value.Type == NodeTypes.ParallelFork))
        {
            if (!joins.TryGetValue(forkId, out var parentJoinId) || fork.ParallelFork?.Branches is not { IsDefault: false } branches)
                return false;
            foreach (var branch in branches)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                bool Walk(string current)
                {
                    if (current == parentJoinId || !visited.Add(current)) return true;
                    if (!definition.Nodes.TryGetValue(current, out var currentNode)) return false;
                    if (currentNode.Type == NodeTypes.ParallelFork && current != forkId)
                    {
                        if (!ownerMap.TryAdd(current, (forkId, branch.Id)) ||
                            !joins.TryGetValue(current, out var childJoinId)) return false;
                        var childContinuation = edges.Where(edge => edge.Source.NodeId == childJoinId &&
                            edge.Source.PortId == "then").ToArray();
                        return childContinuation.Length == 1 && Walk(childContinuation[0].Target.NodeId);
                    }
                    foreach (var edge in edges.Where(edge => edge.Source.NodeId == current))
                    {
                        if (edge.Target.NodeId == parentJoinId) continue;
                        if (!Walk(edge.Target.NodeId)) return false;
                    }
                    return true;
                }

                var branchEntry = edges.Where(edge => edge.Source.NodeId == forkId &&
                    edge.Source.PortId == ParallelBranchLimits.PinId(branch.Id)).ToArray();
                if (branchEntry.Length != 1 || !Walk(branchEntry[0].Target.NodeId)) return false;
            }
        }
        return true;
    }

    private sealed class MutableActivation(string scopeId, string joinNodeId, Guid forkActivationId, long firstSequence,
        ImmutableArray<string> expected, ImmutableArray<ParallelForkActivationIdentity> parentLineage)
    {
        public string ScopeId { get; } = scopeId;
        public string JoinNodeId { get; } = joinNodeId;
        public Guid ForkActivationId { get; } = forkActivationId;
        public long FirstSequence { get; } = firstSequence;
        public ImmutableArray<string> Expected { get; } = expected;
        public ImmutableArray<ParallelForkActivationIdentity> ParentLineage { get; } = parentLineage;
        public Dictionary<string, ImmutableArray<ParallelForkActivationIdentity>> Arrivals { get; } = new(StringComparer.Ordinal);
        public string State { get; set; } = "running";
    }
}
