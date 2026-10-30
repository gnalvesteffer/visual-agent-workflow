using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Validated immutable edits for the supported linear workflow, preserving unrelated identities.</summary>
public static class WorkflowEditing
{
    public static ImmutableArray<string> OrderedNodes(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (WorkflowGraph.HasControlNodes(definition) || WorkflowGraph.HasTypedJson(definition))
            throw new ArgumentException("Linear edits cannot flatten graphs with control or typed-data nodes.", nameof(definition));
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
        if (!WorkflowDraftEditing.HasOnlyDraftableIssues(definition)) WorkflowValidator.ThrowIfInvalid(definition);
        RequireTransformNode(definition, nodeId);
        RequireTransform(type);
        return WorkflowDraftEditing.ValidateEdit(
            definition with { Nodes = definition.Nodes.SetItem(nodeId, new(type, "1.0.0")) });
    }

    public static WorkflowDefinition ReplaceNode(WorkflowDefinition definition, string nodeId, NodeDefinition node)
    {
        if (!WorkflowDraftEditing.HasOnlyDraftableIssues(definition)) WorkflowValidator.ThrowIfInvalid(definition);
        RequireTransformNode(definition, nodeId);
        RequireEditable(node.Type);
        var result = definition with
        {
            SchemaVersion = SchemaFor(definition, node),
            Nodes = definition.Nodes.SetItem(nodeId, node)
        };
        return WorkflowDraftEditing.ValidateEdit(result);
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
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.JsonToArray or NodeTypes.ArrayLength or NodeTypes.TextTemplate))
            throw new ArgumentException("Only registered processing nodes can be edited.", nameof(type));
    }

    internal static string SchemaFor(WorkflowDefinition definition, NodeDefinition node) =>
        node.Type == NodeTypes.NumberAdd && definition.SchemaVersion == WorkflowSchemaVersions.NumberPropagationGraph ? WorkflowSchemaVersions.NumberArithmeticGraph :
        node.Type == NodeTypes.NumberConstant && definition.SchemaVersion == WorkflowSchemaVersions.DirectMcpPinsGraph ? WorkflowSchemaVersions.NumberGraph :
        node.Type == NodeTypes.NumberConstant && definition.SchemaVersion == WorkflowSchemaVersions.IntegerGraph ? WorkflowSchemaVersions.NumberPropagationGraph :
        WorkflowSchemaVersions.UsesExplicitGraph(definition) ? definition.SchemaVersion :
        node.Type == NodeTypes.WorkflowCall ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" ? definition.SchemaVersion : "1.19" :
        node.Type == NodeTypes.Terminate ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" ? definition.SchemaVersion : "1.18" :
        node.Type is NodeTypes.Condition or NodeTypes.Merge ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" or "1.15" or "1.14" or "1.13" or "1.12" or "1.11" or "1.10" or "1.9" or "1.8" or "1.7" or "1.6" or "1.5" or "1.4" ? definition.SchemaVersion : "1.4" :
        node.Type is NodeTypes.Loop or NodeTypes.LoopBody ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" ? definition.SchemaVersion : "1.17" :
        node.Type == NodeTypes.JsonProject && node.JsonProjection?.Fields.Any(field => field.ValueSchema is not null) == true ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" ? definition.SchemaVersion : "1.16" :
        node.Type == NodeTypes.JsonProject && node.JsonProjection?.Fields.Any(field => field.ValueType is not null) == true ? definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" ? definition.SchemaVersion : "1.15" :
        node.Type == NodeTypes.JsonProject && definition.SchemaVersion is not ("1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16") ? "1.13" :
        node.Type == NodeTypes.TextTemplate && definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16") ? "1.7" :
        definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" ? definition.SchemaVersion : definition.SchemaVersion == "1.15" ? "1.15" : definition.SchemaVersion == "1.14" ? "1.14" : definition.SchemaVersion == "1.13" ? "1.13" : definition.SchemaVersion == "1.12" ? "1.12" : definition.SchemaVersion == "1.11" ? "1.11" : definition.SchemaVersion == "1.10" ? "1.10" : definition.SchemaVersion == "1.9" ? "1.9" : definition.SchemaVersion == "1.8" ? "1.8" : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : definition.SchemaVersion == "1.5" ? "1.5" : definition.SchemaVersion == "1.4" ? "1.4" : definition.SchemaVersion == "1.3" || node.Origin is not null ? "1.3" : node.Type == NodeTypes.Agent
            ? "1.2" : node.Type == NodeTypes.McpTool && definition.SchemaVersion == "1.0" ? "1.1" : definition.SchemaVersion;

    private static void RequireTransform(string type)
    {
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse)) throw new ArgumentException("Only approved string transformations can be edited.", nameof(type));
    }
}
