using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Edits the bounded two-branch parallel region as one visual block.</summary>
public static class ParallelBlockEditing
{
    public static ImmutableArray<string> Nodes(WorkflowDefinition definition, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(nodeId, out var selected) ||
            selected.Type is not (NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin))
            throw new ArgumentException("Select a node in a parallel block.", nameof(nodeId));

        var forkId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var joinId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        var members = new HashSet<string>(StringComparer.Ordinal) { forkId, joinId };
        var outgoing = definition.Connections.Values.GroupBy(edge => edge.Source.NodeId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var branchPort in new[] { "branch_1", "branch_2" })
        {
            var first = definition.Connections.Values.Single(edge => edge.Source.NodeId == forkId && edge.Source.PortId == branchPort);
            var current = first.Target.NodeId;
            while (members.Add(current))
            {
                if (current == joinId) break;
                var edge = outgoing[current].Single();
                current = edge.Target.NodeId;
            }
        }

        // The selector inserted by the block keeps a pre-existing string continuation connected.
        var joinOutput = definition.Connections.Values.Single(edge => edge.Source.NodeId == joinId);
        if (definition.Nodes[joinOutput.Target.NodeId].Type == NodeTypes.JsonSelectString &&
            definition.Nodes[joinOutput.Target.NodeId].JsonPropertySelection?.Pointer == "" &&
            outgoing.TryGetValue(joinOutput.Target.NodeId, out var selectorEdges) && selectorEdges.Length == 1)
            members.Add(joinOutput.Target.NodeId);

        return members.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string nodeId)
    {
        var removed = Nodes(definition, nodeId).ToHashSet(StringComparer.Ordinal);
        var forkId = removed.Single(id => definition.Nodes[id].Type == NodeTypes.ParallelFork);
        var joinId = removed.Single(id => definition.Nodes[id].Type == NodeTypes.ParallelJoin);
        var incoming = definition.Connections.Single(entry => entry.Value.Target.NodeId == forkId);
        var joinOutput = definition.Connections.Single(entry => entry.Value.Source.NodeId == joinId);
        var target = joinOutput.Value.Target;
        if (removed.Contains(target.NodeId))
        {
            var selectorOutput = definition.Connections.Single(entry => entry.Value.Source.NodeId == target.NodeId);
            target = selectorOutput.Value.Target;
        }

        var inputType = WorkflowPorts.Outputs(definition.Nodes[incoming.Value.Source.NodeId])
            .Single(port => port.Id == incoming.Value.Source.PortId).DataType;
        var nodes = definition.Nodes.RemoveRange(removed);
        if (nodes[target.NodeId].Type == NodeTypes.Output)
            nodes = nodes.SetItem(target.NodeId, nodes[target.NodeId] with { OutputDataType = inputType == "json" ? "json" : null });

        var connections = definition.Connections.RemoveRange(definition.Connections
            .Where(entry => removed.Contains(entry.Value.Source.NodeId) || removed.Contains(entry.Value.Target.NodeId))
            .Select(entry => entry.Key));
        var id = "link-" + Guid.NewGuid().ToString("N");
        var result = definition with
        {
            Nodes = nodes,
            Connections = connections.Add(id, new(incoming.Value.Source, target, "data"))
        };
        WorkflowValidator.ThrowIfInvalid(result);
        return result;
    }
}
