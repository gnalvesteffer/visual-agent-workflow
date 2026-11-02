using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Identifies the bounded data-feedback loop shape used by legacy workflows.</summary>
public sealed record LoopDataTopology(
    string LoopNodeId,
    string BodyAnchorNodeId,
    string InitialInputConnectionId,
    string BodyReturnConnectionId);

/// <summary>Recognizes the one bounded loop whose value is carried through data connections.</summary>
public static class LoopDataTopologyAnalyzer
{
    public const int MaximumBodyNodes = 256;

    /// <summary>Returns the data-loop topology when its single body path and feedback edge are unambiguous.</summary>
    public static LoopDataTopology? Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Nodes.Values.Any(node => node is null) || definition.Connections.Values.Any(edge =>
                edge is null || edge.Source is null || edge.Target is null))
            return null;

        var loops = definition.Nodes.Where(pair => pair.Value.Type == NodeTypes.Loop).ToArray();
        var anchors = definition.Nodes.Where(pair => pair.Value.Type == NodeTypes.LoopBody).ToArray();
        if (loops.Length != 1 || anchors.Length != 1) return null;

        var loopId = loops[0].Key;
        var anchorId = anchors[0].Key;
        var bodyStart = definition.Connections.Where(pair => pair.Value.Source == new PortReference(loopId, "body")).ToArray();
        if (bodyStart.Length != 1 || bodyStart[0].Value.Target != new PortReference(anchorId, "in")) return null;

        var bodyReturnId = FindBodyReturn(definition, loopId, anchorId);
        if (bodyReturnId is null) return null;

        var loopInputs = definition.Connections.Where(pair => pair.Value.Target == new PortReference(loopId, "in")).ToArray();
        if (loopInputs.Length != 2 || !loopInputs.Any(pair => pair.Key == bodyReturnId)) return null;
        var initialInput = loopInputs.SingleOrDefault(pair => pair.Key != bodyReturnId);
        if (initialInput.Value is null) return null;

        return new(loopId, anchorId, initialInput.Key, bodyReturnId);
    }

    private static string? FindBodyReturn(WorkflowDefinition definition, string loopId, string anchorId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = anchorId;
        while (visited.Add(current) && visited.Count <= MaximumBodyNodes)
        {
            if (!definition.Nodes.TryGetValue(current, out var node) || node is null ||
                node.Type is not (NodeTypes.LoopBody or NodeTypes.Uppercase or NodeTypes.Reverse) ||
                current != anchorId && node.Type == NodeTypes.LoopBody)
                return null;

            var outgoing = definition.Connections.Where(pair => pair.Value.Source.NodeId == current).ToArray();
            if (outgoing.Length != 1) return null;
            var edge = outgoing[0];
            if (edge.Value.Target == new PortReference(loopId, "in")) return edge.Key;
            current = edge.Value.Target.NodeId;
        }

        return null;
    }
}
