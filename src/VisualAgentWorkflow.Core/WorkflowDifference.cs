using System.Collections.Immutable;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowChange(string Kind, string Id, string? Before, string? After);

public static class WorkflowDifference
{
    /// <summary>Structural comparison by stable identity; independent of dictionary serialization order.</summary>
    public static ImmutableArray<WorkflowChange> Compare(WorkflowDefinition before, WorkflowDefinition after)
    {
        WorkflowValidator.ThrowIfInvalid(before);
        WorkflowValidator.ThrowIfInvalid(after);
        var changes = ImmutableArray.CreateBuilder<WorkflowChange>();
        foreach (var id in before.Nodes.Keys.Union(after.Nodes.Keys).Order(StringComparer.Ordinal))
        {
            before.Nodes.TryGetValue(id, out var oldNode);
            after.Nodes.TryGetValue(id, out var newNode);
            if (DescribeNode(oldNode) != DescribeNode(newNode)) changes.Add(new("node", id, DescribeNode(oldNode), DescribeNode(newNode)));
        }
        foreach (var id in before.Connections.Keys.Union(after.Connections.Keys).Order(StringComparer.Ordinal))
        {
            before.Connections.TryGetValue(id, out var oldEdge);
            after.Connections.TryGetValue(id, out var newEdge);
            if (oldEdge != newEdge) changes.Add(new("connection", id, Describe(oldEdge), Describe(newEdge)));
        }
        if (before.WorkflowId != after.WorkflowId) changes.Add(new("workflow", "workflowId", before.WorkflowId, after.WorkflowId));
        if (before.EntryNodeId != after.EntryNodeId) changes.Add(new("workflow", "entryNodeId", before.EntryNodeId, after.EntryNodeId));
        if (before.SchemaVersion != after.SchemaVersion) changes.Add(new("workflow", "schemaVersion", before.SchemaVersion, after.SchemaVersion));
        return changes.ToImmutable();
    }

    private static string? Describe(ConnectionDefinition? edge) => edge is null ? null :
        $"{edge.Source.NodeId}.{edge.Source.PortId} → {edge.Target.NodeId}.{edge.Target.PortId} ({edge.Kind})";

    private static string? DescribeNode(NodeDefinition? node) => node is null ? null :
        JsonSerializer.Serialize(node, JsonSerializerOptions.Web);
}
