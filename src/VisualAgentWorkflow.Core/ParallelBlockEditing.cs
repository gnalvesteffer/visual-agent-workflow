using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Edits the bounded two-branch parallel region as one visual block.</summary>
public static class ParallelBlockEditing
{
    public static ImmutableArray<string> Nodes(WorkflowDefinition definition, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.SchemaVersion == WorkflowSchemaVersions.ConfigurableParallelGraph)
            return ConfigurableNodes(definition, nodeId);
        if (!definition.Nodes.TryGetValue(nodeId, out var selected) ||
            selected.Type is not (NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin))
            throw new ArgumentException("Select a node in a parallel block.", nameof(nodeId));

        var outgoing = definition.Connections.Values.GroupBy(edge => edge.Source.NodeId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var forkId in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelFork).Select(entry => entry.Key))
        {
            var members = new HashSet<string>(StringComparer.Ordinal) { forkId };
            string? joinId = null;
            foreach (var branchPort in new[] { "branch_1", "branch_2" })
            {
                var first = definition.Connections.Values.Single(edge => edge.Source.NodeId == forkId && edge.Source.PortId == branchPort);
                var current = first.Target.NodeId;
                while (members.Add(current))
                {
                    if (definition.Nodes[current].Type == NodeTypes.ParallelJoin)
                    {
                        joinId = current;
                        break;
                    }
                    var edge = outgoing[current].Single();
                    current = edge.Target.NodeId;
                }
            }
            if (joinId is null) continue;
            members.Add(joinId);

            // The selector inserted by the block keeps a pre-existing string continuation connected.
            var joinOutput = definition.Connections.Values.Single(edge => edge.Source.NodeId == joinId);
            if (definition.Nodes[joinOutput.Target.NodeId].Type == NodeTypes.JsonSelectString &&
                definition.Nodes[joinOutput.Target.NodeId].JsonPropertySelection?.Pointer == "" &&
                outgoing.TryGetValue(joinOutput.Target.NodeId, out var selectorEdges) && selectorEdges.Length == 1)
                members.Add(joinOutput.Target.NodeId);

            if (members.Contains(nodeId)) return members.Order(StringComparer.Ordinal).ToImmutableArray();
        }
        throw new ArgumentException("The selected node does not belong to a valid parallel block.", nameof(nodeId));
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string nodeId)
    {
        if (definition.SchemaVersion == WorkflowSchemaVersions.ConfigurableParallelGraph)
            return RemoveConfigurable(definition, nodeId);
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

    private static ImmutableArray<string> ConfigurableNodes(WorkflowDefinition definition, string nodeId)
    {
        if (!definition.Nodes.TryGetValue(nodeId, out var selected) ||
            selected.Type is not (NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.Agent))
            throw new ArgumentException("Select a node inside a configurable parallel region.", nameof(nodeId));
        var forkEntry = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork);
        var joinEntry = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelJoin);
        var branches = forkEntry.Value.ParallelFork?.Branches ?? [];
        var members = new HashSet<string>(StringComparer.Ordinal) { forkEntry.Key, joinEntry.Key };
        foreach (var branch in branches)
        {
            var current = branch.AnchorNodeId;
            for (var step = 0; step < definition.Nodes.Count; step++)
            {
                if (!members.Add(current) && current != branch.AnchorNodeId)
                    throw new ArgumentException("The parallel region contains an overlapping branch.", nameof(nodeId));
                var edge = definition.ExecutionConnections!.Values.Single(candidate => candidate.Source.NodeId == current);
                if (edge.Target.NodeId == joinEntry.Key) break;
                if (!definition.Nodes.TryGetValue(edge.Target.NodeId, out var target) || target.Type != NodeTypes.Agent)
                    throw new ArgumentException("The parallel region contains an unsupported branch node.", nameof(nodeId));
                current = edge.Target.NodeId;
            }
        }
        return members.Contains(nodeId)
            ? members.Order(StringComparer.Ordinal).ToImmutableArray()
            : throw new ArgumentException("The selected node does not belong to the configurable parallel region.", nameof(nodeId));
    }

    private static WorkflowDefinition RemoveConfigurable(WorkflowDefinition definition, string nodeId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var removed = ConfigurableNodes(definition, nodeId).ToHashSet(StringComparer.Ordinal);
        var forkId = removed.Single(id => definition.Nodes[id].Type == NodeTypes.ParallelFork);
        var joinId = removed.Single(id => definition.Nodes[id].Type == NodeTypes.ParallelJoin);
        var executionConnections = definition.ExecutionConnections ?? throw new ArgumentException("The configurable graph has no execution connections.", nameof(definition));
        var incoming = executionConnections.Single(edge => edge.Value.Target.NodeId == forkId);
        var continuation = executionConnections.Single(edge => edge.Value.Source.NodeId == joinId);
        var output = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output);
        var inputId = definition.EntryNodeId;
        var nodes = definition.Nodes.RemoveRange(removed)
            .SetItem(output.Key, definition.Nodes[output.Key] with { OutputDataType = null });
        var execution = executionConnections.RemoveRange(executionConnections
            .Where(edge => removed.Contains(edge.Value.Source.NodeId) || removed.Contains(edge.Value.Target.NodeId))
            .Select(edge => edge.Key))
            .Add("link-" + Guid.NewGuid().ToString("N"), new(incoming.Value.Source, continuation.Value.Target, "execution"));
        var data = definition.Connections.RemoveRange(definition.Connections
            .Where(edge => removed.Contains(edge.Value.Source.NodeId) || removed.Contains(edge.Value.Target.NodeId))
            .Select(edge => edge.Key))
            .Add("link-" + Guid.NewGuid().ToString("N"), new(new(inputId, "out"), new(output.Key, "in"), "data"));
        var result = definition with
        {
            SchemaVersion = WorkflowSchemaVersions.IntegerGraph,
            Nodes = nodes,
            Connections = data,
            ExecutionConnections = execution
        };
        WorkflowValidator.ThrowIfInvalid(result);
        return result;
    }
}
