using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Validated immutable edits for the supported linear pipeline, preserving unrelated identities.</summary>
public static class PipelineEditing
{
    public static ImmutableArray<string> OrderedNodes(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (WorkflowGraph.HasControlNodes(definition)) throw new ArgumentException("Linear edits cannot flatten graphs with control nodes.", nameof(definition));
        var successors = definition.Connections.Values.ToDictionary(e => e.Source.NodeId, e => e.Target.NodeId);
        var result = ImmutableArray.CreateBuilder<string>();
        var current = definition.EntryNodeId;
        while (true)
        {
            result.Add(current);
            if (!successors.TryGetValue(current, out var next)) break;
            current = next;
        }
        return result.ToImmutable();
    }

    public static WorkflowDefinition InsertAfter(WorkflowDefinition definition, string afterNodeId, string type)
    {
        RequireTransform(type);
        return InsertNodeAfter(definition, afterNodeId, new(type, "1.0.0"));
    }

    public static WorkflowDefinition InsertNodeAfter(WorkflowDefinition definition, string afterNodeId, NodeDefinition node)
    {
        RequireEditable(node.Type);
        var order = OrderedNodes(definition).ToList();
        var position = order.IndexOf(afterNodeId);
        if (position < 0 || position == order.Count - 1) throw new ArgumentException("Insert after an existing non-output node.", nameof(afterNodeId));
        var id = "node-" + Guid.NewGuid().ToString("N");
        order.Insert(position + 1, id);
        return Reconnect(definition with
        {
            SchemaVersion = SchemaFor(definition, node),
            Nodes = definition.Nodes.Add(id, node)
        }, order);
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string nodeId)
    {
        var order = OrderedNodes(definition).ToList();
        RequireTransformNode(definition, nodeId);
        if (definition.Nodes[nodeId].Type == NodeTypes.JsonSelectString)
            return GraphEditing.RemoveJsonSelector(definition, nodeId);
        order.Remove(nodeId);
        return Reconnect(definition with { Nodes = definition.Nodes.Remove(nodeId) }, order);
    }

    public static WorkflowDefinition Move(WorkflowDefinition definition, string nodeId, int direction)
    {
        var order = OrderedNodes(definition).ToList();
        RequireTransformNode(definition, nodeId);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var index = order.IndexOf(nodeId);
        var destination = index + direction;
        if (destination <= 0 || destination >= order.Count - 1)
            throw new ArgumentException("A transformation cannot move outside the input/output terminals.");
        (order[index], order[destination]) = (order[destination], order[index]);
        return Reconnect(definition, order);
    }

    public static WorkflowDefinition ChangeOperation(WorkflowDefinition definition, string nodeId, string type)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        RequireTransformNode(definition, nodeId);
        RequireTransform(type);
        return definition with { Nodes = definition.Nodes.SetItem(nodeId, new(type, "1.0.0")) };
    }

    public static WorkflowDefinition ReplaceNode(WorkflowDefinition definition, string nodeId, NodeDefinition node)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        RequireTransformNode(definition, nodeId);
        RequireEditable(node.Type);
        var result = definition with
        {
            SchemaVersion = SchemaFor(definition, node),
            Nodes = definition.Nodes.SetItem(nodeId, node)
        };
        WorkflowValidator.ThrowIfInvalid(result);
        return result;
    }

    internal static WorkflowDefinition Reconnect(WorkflowDefinition definition, IReadOnlyList<string> order)
    {
        var existing = definition.Connections.ToDictionary(e => (e.Value.Source.NodeId, e.Value.Target.NodeId), e => e.Key);
        var connections = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count - 1; i++)
        {
            var id = existing.GetValueOrDefault((order[i], order[i + 1])) ?? "link-" + Guid.NewGuid().ToString("N");
            connections[id] = new(new(order[i], "out"), new(order[i + 1], "in"), "data");
        }
        var result = definition with { Connections = connections.ToImmutable() };
        WorkflowValidator.ThrowIfInvalid(result);
        return result;
    }

    private static void RequireTransformNode(WorkflowDefinition definition, string nodeId)
    {
        if (!definition.Nodes.TryGetValue(nodeId, out var node)) throw new ArgumentException("Unknown node.", nameof(nodeId));
        RequireEditable(node.Type);
    }

    private static void RequireEditable(string type)
    {
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString))
            throw new ArgumentException("Only registered processing nodes can be edited.", nameof(type));
    }

    internal static string SchemaFor(WorkflowDefinition definition, NodeDefinition node) =>
        definition.SchemaVersion == "1.9" ? "1.9" : definition.SchemaVersion == "1.8" ? "1.8" : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : definition.SchemaVersion == "1.5" ? "1.5" : definition.SchemaVersion == "1.4" ? "1.4" : definition.SchemaVersion == "1.3" || node.Origin is not null ? "1.3" : node.Type == NodeTypes.Agent
            ? "1.2" : node.Type == NodeTypes.McpTool && definition.SchemaVersion == "1.0" ? "1.1" : definition.SchemaVersion;

    private static void RequireTransform(string type)
    {
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse)) throw new ArgumentException("Only approved string transformations can be edited.", nameof(type));
    }
}
