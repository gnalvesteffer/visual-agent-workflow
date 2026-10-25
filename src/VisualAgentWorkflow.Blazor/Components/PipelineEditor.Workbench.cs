using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class PipelineEditor
{
    private string? insertionConnection;
    private ImmutableArray<WorkflowPort> SelectedOutputs => selectedNode is { } id && Definition.Nodes.TryGetValue(id, out var node) ? WorkflowPorts.Outputs(node) : [];
    private Task ConnectionSelected(string id)
    {
        if (Definition.Connections.TryGetValue(id, out var edge))
        { insertionConnection = id; selectedNode = edge.Source.NodeId; selectedPort = edge.Source.PortId; }
        return Task.CompletedTask;
    }

    private PortReference? Insertion(GraphContextRequest? context)
    {
        var connection = context?.ConnectionId ?? (context?.NodeId is null ? insertionConnection : null);
        if (connection is not null && Definition.Connections.TryGetValue(connection, out var edge)) return edge.Source;
        var id = context?.NodeId ?? selectedNode ?? Definition.EntryNodeId;
        if (!Definition.Nodes.TryGetValue(id, out var node)) return null;
        if (node.Type is NodeTypes.Output or NodeTypes.Approval)
            return Definition.Connections.Values.Single(e => e.Target.NodeId == id).Source;
        var ports = WorkflowPorts.Outputs(node);
        return ports.Length == 0 ? null : new(id, ports.Any(p => p.Id == selectedPort) ? selectedPort : ports[0].Id);
    }

    private string InsertionDescription(GraphContextRequest? context) => Insertion(context) is { } source
        ? $"Insert after {(string.IsNullOrWhiteSpace(EffectiveLayout.Nodes[source.NodeId].Label) ? WorkflowGraphCanvas.FriendlyName(Definition.Nodes[source.NodeId]) : EffectiveLayout.Nodes[source.NodeId].Label)} · {source.PortId}"
        : "Select an insertion path.";

    private IReadOnlyList<WorkflowNodeLibraryEntry> LibraryEntries(GraphContextRequest? context = null)
    {
        var enabled = !ReadOnly && !Disabled && Definition.Nodes.Count < 256 && Insertion(context) is not null;
        var selected = context?.NodeId ?? selectedNode;
        List<WorkflowNodeLibraryEntry> entries =
        [
            new(NodeTypes.Uppercase, "Uppercase", "Text processing", "Convert text deterministically", enabled),
            new(NodeTypes.Reverse, "Reverse", "Text processing", "Reverse text deterministically", enabled),
            new(NodeTypes.TextTemplate, "Text template", "Text processing", "Build deterministic text from input and workflow values", enabled),
            new(NodeTypes.WorkflowValue, "Workflow value", "Inputs", "Pass a configured workflow parameter into the graph", enabled && ParameterDefinitions.Count > 0),
            new("condition", "Condition", "Flow", "Create true / false paths and a merge", enabled && Definition.Nodes.Count <= 254),
            new("approval", "Approval", "Flow", "Select an MCP tool to require exact-action review", enabled && selected is not null && Definition.Nodes.TryGetValue(selected, out var selectedDefinition) && selectedDefinition.Type == NodeTypes.McpTool && !GuardedTool(selected))
        ];
        entries.AddRange(Providers.Select(p => new WorkflowNodeLibraryEntry(ProviderKey(p), p.DisplayName, "Agents", $"{p.ModelId} · {p.Version}", enabled)));
        entries.AddRange(Bindings.Select(b => new WorkflowNodeLibraryEntry(ToolKey(b), b.DisplayName, "MCP tools", "Host-registered tool; node grants still apply", enabled)));
        return entries;
    }

    private async Task AddLibraryNode(string key, GraphContextRequest? context = null)
    {
        if (!LibraryEntries(context).Any(e => e.Key == key && e.Enabled)) return;
        var insertion = Insertion(context);
        if (insertion is null) return;
        await CloseMenu(context);
        if (key == "approval")
        {
            var target = context?.NodeId ?? selectedNode!;
            await Edit(() => GraphEditing.InsertApprovalBefore(Definition, target), context, insertion);
        }
        else if (key == "condition")
            await Edit(() => GraphEditing.InsertConditionalAfter(Definition, insertion.NodeId, insertion.PortId), context, insertion);
        else
            await Edit(() => HasRouting ? GraphEditing.InsertNodeAfter(Definition, insertion.NodeId, insertion.PortId, FromSelection(key)) :
                PipelineEditing.InsertNodeAfter(Definition, insertion.NodeId, FromSelection(key)), context, insertion);
        insertionConnection = null;
    }

    private bool CanRemove(string id) => !ReadOnly && !Disabled && Definition.Nodes.TryGetValue(id, out var node) &&
        node.Type is not (NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge) && !GuardedTool(id);
    private bool CanDuplicate(string id) => CanRemove(id) && Definition.Nodes[id].Type != NodeTypes.Approval;
    private async Task RemoveSelected(string id, GraphContextRequest? context = null)
    {
        await CloseMenu(context);
        if (!CanRemove(id)) { error = "This node cannot be deleted independently. Input/output and branch boundaries are required; remove an approval before its guarded tool."; return; }
        await Edit(() => Definition.Nodes[id].Type == NodeTypes.Approval ? GraphEditing.RemoveApproval(Definition, id) :
            HasRouting ? GraphEditing.RemoveNode(Definition, id) : PipelineEditing.Remove(Definition, id));
    }

    private async Task Duplicate(string id, GraphContextRequest context)
    {
        if (!CanDuplicate(id)) return;
        await CloseMenu(context);
        var source = new PortReference(id, "out");
        await Edit(() => HasRouting ? GraphEditing.InsertNodeAfter(Definition, id, "out", Definition.Nodes[id]) :
            PipelineEditing.InsertNodeAfter(Definition, id, Definition.Nodes[id]), context, source);
    }

    private Task CloseMenu(GraphContextRequest? context) => context is not null && canvas is not null ? canvas.CloseContextMenu(context.Id) : Task.CompletedTask;
    private async Task Reroute(GraphContextRequest context)
    {
        if (canvas is null || context.ConnectionId is null) return;
        await canvas.AddWaypoint(context.ConnectionId, context.Segment, context.Position.X, context.Position.Y);
        await CloseMenu(context);
    }

    private WorkflowLayout PlaceAddedNodes(WorkflowDefinition next, WorkflowLayout presentation, string[] added, GraphContextRequest? context, PortReference? insertion)
    {
        if (added.Length == 0) return presentation;
        var anchor = selectedNode is { } id && added.Contains(id) ? id : added[0];
        var original = presentation.Nodes[anchor].Position;
        var desired = context?.Position ?? (insertion is not null ? new GraphPoint(EffectiveLayout.Nodes[insertion.NodeId].Position.X + 360, EffectiveLayout.Nodes[insertion.NodeId].Position.Y) : original);
        var minimumX = added.Min(nodeId => presentation.Nodes[nodeId].Position.X - original.X);
        var minimumY = added.Min(nodeId => presentation.Nodes[nodeId].Position.Y - original.Y);
        var maximumX = added.Max(nodeId => presentation.Nodes[nodeId].Position.X - original.X);
        var maximumY = added.Max(nodeId => presentation.Nodes[nodeId].Position.Y - original.Y);
        var x = Math.Clamp(desired.X, Math.Max(0, -minimumX), WorkflowLayoutJson.MaximumCoordinate - Math.Max(0, maximumX));
        var y = Math.Clamp(desired.Y, Math.Max(0, -minimumY), WorkflowLayoutJson.MaximumCoordinate - Math.Max(0, maximumY));
        var nodes = presentation.Nodes;
        foreach (var nodeId in added)
        {
            var previous = nodes[nodeId];
            nodes = nodes.SetItem(nodeId, previous with { Position = new(x + previous.Position.X - original.X, y + previous.Position.Y - original.Y) });
        }
        var wires = presentation.Wires;
        if (context is null && insertion is not null)
        {
            // Clicking the library opens space on the chosen path; dropping preserves the user's exact placement.
            var edge = Definition.Connections.Values.Single(e => e.Source == insertion);
            var downstream = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(); queue.Enqueue(edge.Target.NodeId);
            while (queue.TryDequeue(out var target))
                if (downstream.Add(target)) foreach (var outgoing in Definition.Connections.Values.Where(e => e.Source.NodeId == target)) queue.Enqueue(outgoing.Target.NodeId);
            var shift = maximumX - minimumX + 360;
            if (downstream.All(target => nodes[target].Position.X + shift <= WorkflowLayoutJson.MaximumCoordinate))
            {
                foreach (var target in downstream)
                    nodes = nodes.SetItem(target, nodes[target] with { Position = new(nodes[target].Position.X + shift, nodes[target].Position.Y) });
                foreach (var wire in wires.Keys.Where(wireId => downstream.Contains(next.Connections[wireId].Source.NodeId) || downstream.Contains(next.Connections[wireId].Target.NodeId)).ToArray())
                    wires = wires.Remove(wire);
            }
        }
        return presentation with { Nodes = nodes, Wires = wires };
    }
}
