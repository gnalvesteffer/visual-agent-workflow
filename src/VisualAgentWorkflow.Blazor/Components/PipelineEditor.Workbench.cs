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
        if (node.Type is NodeTypes.ParallelFork or NodeTypes.Loop) return null;
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
        var insertion = Insertion(context);
        var sourceType = insertion is { } source
            ? WorkflowPorts.Outputs(Definition.Nodes[source.NodeId]).Single(port => port.Id == source.PortId).DataType
            : null;
        var canInsert = !ReadOnly && !Disabled && Definition.Nodes.Count < 256 && sourceType is not null;
        var textPath = canInsert && sourceType == "string";
        var jsonPath = canInsert && sourceType == "json";
        var jsonSelectorWouldBreakPath = insertion is { } jsonSource &&
            Definition.Nodes[jsonSource.NodeId].Type == NodeTypes.ParallelJoin &&
            Definition.Connections.Values.FirstOrDefault(edge => edge.Source == jsonSource) is { } joinEdge &&
            Definition.Nodes[joinEdge.Target.NodeId].Type == NodeTypes.JsonSelectString;
        var selected = context?.NodeId ?? selectedNode;
        var hasLoop = Definition.Nodes.Values.Any(node => node.Type == NodeTypes.Loop);
        var canInsertCondition = textPath && Definition.Nodes.Count <= 254 && !hasLoop && !IsParallelBranchPath(insertion);
        var canInsertLoop = textPath && Definition.Nodes.Count <= 254 && !WorkflowGraph.HasControlNodes(Definition) && !WorkflowGraph.HasTypedJson(Definition);
        List<WorkflowNodeLibraryEntry> entries =
        [
            new(NodeTypes.Uppercase, "Uppercase", "Text processing", "Convert text deterministically", textPath),
            new(NodeTypes.Reverse, "Reverse", "Text processing", "Reverse text deterministically", textPath),
            new(NodeTypes.TextTemplate, "Text template", "Text processing", "Build deterministic text from input and workflow values", textPath),
            new(NodeTypes.WorkflowValue, "Workflow value", "Inputs", "Pass a configured workflow parameter into the graph", textPath && ParameterDefinitions.Count > 0),
            new(NodeTypes.JsonProject, "Project JSON fields", "Data", "Build a bounded JSON object from selected JSON Pointer values", jsonPath),
            new("json-selector", "Select JSON value as text", "Data", "Convert a selected JSON value to text", jsonPath && !jsonSelectorWouldBreakPath),
            new("parallel", "Parallel block", "Flow", "Run two visible branch paths concurrently and collect named results", textPath && Definition.Nodes.Count <= 251 && !hasLoop && !Definition.Nodes.Values.Any(node => node.Type == NodeTypes.ParallelFork)),
            new("condition", "Condition", "Flow", "Create true / false paths; merge only when branches rejoin", canInsertCondition),
            new("loop", "Bounded loop", "Flow", "Repeat one linear body while a text condition holds, up to a fixed iteration limit", canInsertLoop),
            new("terminate", "Terminate workflow", "Flow", "End this path successfully without returning a workflow result", textPath && Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Terminate) < 16),
            new("approval", "Approval", "Flow", "Select an MCP tool to require exact-action review", !hasLoop && textPath && selected is not null && Definition.Nodes.TryGetValue(selected, out var selectedDefinition) && selectedDefinition.Type == NodeTypes.McpTool && !GuardedTool(selected))
        ];
        entries.AddRange(Providers.Select(p => new WorkflowNodeLibraryEntry(ProviderKey(p), p.DisplayName, "Agents", $"{p.ModelId} · {p.Version}", textPath)));
        entries.AddRange(Bindings.Select(b => new WorkflowNodeLibraryEntry(ToolKey(b), b.DisplayName, "MCP tools", "Host-registered tool; node grants still apply", textPath)));
        return entries;
    }

    private bool IsParallelBranchPath(PortReference? insertion)
    {
        if (insertion is null) return false;
        var fork = Definition.Nodes.FirstOrDefault(entry => entry.Value.Type == NodeTypes.ParallelFork);
        if (fork.Value is null) return false;
        var joinId = Definition.Nodes.FirstOrDefault(entry => entry.Value.Type == NodeTypes.ParallelJoin).Key;
        if (joinId is null) return false;
        var pending = new Queue<string>();
        foreach (var edge in Definition.Connections.Values.Where(edge => edge.Source.NodeId == fork.Key)) pending.Enqueue(edge.Target.NodeId);
        var pathNodes = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var id))
        {
            if (id == joinId || !pathNodes.Add(id)) continue;
            foreach (var edge in Definition.Connections.Values.Where(edge => edge.Source.NodeId == id)) pending.Enqueue(edge.Target.NodeId);
        }
        return pathNodes.Contains(insertion.NodeId);
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
        else if (key == "parallel")
            await Edit(() => GraphEditing.InsertParallelBlockAfter(Definition, insertion.NodeId, insertion.PortId), context, insertion);
        else if (key == "loop")
            await Edit(() => GraphEditing.InsertBoundedLoopAfter(Definition, insertion.NodeId, insertion.PortId), context, insertion);
        else if (key == "terminate")
            await Edit(() => GraphEditing.InsertTerminationAfter(Definition, insertion.NodeId, insertion.PortId), context, insertion);
        else if (key == "json-selector")
            await Edit(() => GraphEditing.InsertJsonSelectorAfterJson(Definition, insertion.NodeId, insertion.PortId), context, insertion);
        else
            await Edit(() => UsesGraphEditing ? GraphEditing.InsertNodeAfter(Definition, insertion.NodeId, insertion.PortId, FromSelection(key)) :
                PipelineEditing.InsertNodeAfter(Definition, insertion.NodeId, FromSelection(key)), context, insertion);
        insertionConnection = null;
    }

    private bool CanRemove(string id) => !ReadOnly && !Disabled && Definition.Nodes.TryGetValue(id, out var node) &&
        node.Type is not (NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.LoopBody) && !GuardedTool(id);
    private bool CanDuplicate(string id) => CanRemove(id) && Definition.Nodes[id].Type is not
        (NodeTypes.Loop or NodeTypes.Approval or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);
    private async Task RemoveSelected(string id, GraphContextRequest? context = null)
    {
        await CloseMenu(context);
        if (!ReadOnly && !Disabled && Definition.Nodes.TryGetValue(id, out var node) && node.Type == NodeTypes.Condition)
        { await RequestBlockRemoval(id); return; }
        if (!ReadOnly && !Disabled && Definition.Nodes.TryGetValue(id, out node) &&
            node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin)
        { await RequestParallelBlockRemoval(id); return; }
        if (!CanRemove(id)) { error = "This node cannot be deleted independently. Input/output and branch boundaries are required; remove an approval before its guarded tool."; return; }
        await Edit(() => Definition.Nodes[id].Type == NodeTypes.Approval ? GraphEditing.RemoveApproval(Definition, id) :
            UsesGraphEditing ? GraphEditing.RemoveNode(Definition, id) : PipelineEditing.Remove(Definition, id));
    }

    private string? pendingBlockRemoval;
    private ImmutableArray<string> pendingBlockNodes = [];
    private string? pendingParallelBlockRemoval;
    private ImmutableArray<string> pendingParallelBlockNodes = [];
    private async Task RequestBlockRemoval(string id, GraphContextRequest? context = null)
    {
        if (ReadOnly || Disabled) return;
        await CloseMenu(context);
        CancelBlockRemoval();
        try
        {
            pendingBlockNodes = ConditionalBlockEditing.Nodes(Definition, id);
            pendingBlockRemoval = id;
            await SelectNode(id);
            error = null;
        }
        catch (ArgumentException exception) { error = exception.Message; }
    }

    private void CancelBlockRemoval() { pendingBlockRemoval = null; pendingBlockNodes = []; }

    private async Task RequestParallelBlockRemoval(string id, GraphContextRequest? context = null)
    {
        if (ReadOnly || Disabled) return;
        await CloseMenu(context);
        CancelBlockRemoval();
        try
        {
            pendingParallelBlockNodes = ParallelBlockEditing.Nodes(Definition, id);
            pendingParallelBlockRemoval = Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
            await SelectNode(pendingParallelBlockRemoval);
            error = null;
        }
        catch (ArgumentException exception) { error = exception.Message; }
    }

    private void CancelParallelBlockRemoval() { pendingParallelBlockRemoval = null; pendingParallelBlockNodes = []; }

    private async Task ConfirmParallelBlockRemoval()
    {
        if (pendingParallelBlockRemoval is not { } id || ReadOnly || Disabled) return;
        await Edit(() => ParallelBlockEditing.Remove(Definition, id));
        CancelParallelBlockRemoval();
    }

    private async Task ConfirmBlockRemoval()
    {
        if (pendingBlockRemoval is not { } id || ReadOnly || Disabled) return;
        await Edit(() => ConditionalBlockEditing.Remove(Definition, id));
        CancelBlockRemoval();
    }

    private async Task Duplicate(string id, GraphContextRequest context)
    {
        if (!CanDuplicate(id)) return;
        await CloseMenu(context);
        var source = new PortReference(id, "out");
        await Edit(() => UsesGraphEditing ? GraphEditing.InsertNodeAfter(Definition, id, "out", Definition.Nodes[id]) :
            PipelineEditing.InsertNodeAfter(Definition, id, Definition.Nodes[id]), context, source);
    }

    private Task CloseMenu(GraphContextRequest? context) => context is not null && canvas is not null ? canvas.CloseContextMenu(context.Id) : Task.CompletedTask;
    private async Task Reroute(GraphContextRequest context)
    {
        if (canvas is null || context.ConnectionId is null) return;
        await canvas.AddWaypoint(context.ConnectionId, context.Segment, context.Position.X, context.Position.Y);
        await CloseMenu(context);
    }

    private async Task Disconnect(string connectionId, GraphContextRequest context)
    {
        await CloseMenu(context);
        await Edit(() => GraphEditing.Disconnect(Definition, connectionId));
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
