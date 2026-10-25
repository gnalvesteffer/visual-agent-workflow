using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Explicit removal of isolated, single-entry/single-exit conditional regions.</summary>
public static class ConditionalBlockEditing
{
    /// <summary>Returns the condition, both arms (including nested decisions), and their matching merge.</summary>
    public static ImmutableArray<string> Nodes(WorkflowDefinition definition, string conditionId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(conditionId, out var condition) || condition.Type != NodeTypes.Condition)
            throw new ArgumentException("Choose a condition node to remove its block.", nameof(conditionId));

        var order = WorkflowGraph.TopologicalOrder(definition);
        var successors = definition.Nodes.Keys.ToDictionary(id => id,
            id => definition.Connections.Values.Where(edge => edge.Source.NodeId == id).Select(edge => edge.Target.NodeId).ToArray());
        var commonContinuations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in order.Reverse())
        {
            // A matching merge must occur on every path, not merely be reachable from both arms.
            var next = successors[id];
            var continuations = next.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) : new(commonContinuations[next[0]], StringComparer.Ordinal);
            foreach (var target in next.Skip(1)) continuations.IntersectWith(commonContinuations[target]);
            continuations.Add(id);
            commonContinuations.Add(id, continuations);
        }
        var mergeId = order.FirstOrDefault(id => id != conditionId && commonContinuations[conditionId].Contains(id));
        if (mergeId is null || definition.Nodes[mergeId].Type != NodeTypes.Merge)
            throw new ArgumentException("This condition has no isolated matching merge.", nameof(conditionId));

        var block = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(conditionId);
        while (pending.TryPop(out var id))
        {
            if (!block.Add(id) || id == mergeId) continue;
            foreach (var next in successors[id]) pending.Push(next);
        }
        if (definition.Connections.Values.Any(edge => block.Contains(edge.Target.NodeId) &&
            edge.Target.NodeId != conditionId && !block.Contains(edge.Source.NodeId)))
            throw new ArgumentException("This block shares a branch or merge with another path and cannot be removed independently.", nameof(conditionId));

        return order.Where(block.Contains).ToImmutableArray();
    }

    /// <summary>Bypasses the entire block, retaining the incoming connection identity and unrelated graph content.</summary>
    public static WorkflowDefinition Remove(WorkflowDefinition definition, string conditionId)
    {
        var block = Nodes(definition, conditionId).ToHashSet(StringComparer.Ordinal);
        var incoming = definition.Connections.Single(edge => edge.Value.Target.NodeId == conditionId);
        var outgoing = definition.Connections.Single(edge => block.Contains(edge.Value.Source.NodeId) && !block.Contains(edge.Value.Target.NodeId));
        var connections = definition.Connections.RemoveRange(definition.Connections
            .Where(edge => block.Contains(edge.Value.Source.NodeId) || block.Contains(edge.Value.Target.NodeId)).Select(edge => edge.Key))
            .Add(incoming.Key, new(incoming.Value.Source, outgoing.Value.Target, "data"));
        var result = definition with { Nodes = definition.Nodes.RemoveRange(block), Connections = connections };
        WorkflowValidator.ThrowIfInvalid(result);
        return result;
    }
}
