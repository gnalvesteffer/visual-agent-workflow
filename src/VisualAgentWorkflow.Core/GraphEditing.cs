namespace VisualAgentWorkflow.Core;

/// <summary>Validated local edge edits. No topological order is treated as an execution sequence.</summary>
public static class GraphEditing
{
    public static WorkflowDefinition InsertConditionalAfter(WorkflowDefinition definition, string sourceId, string portId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var edge = Outgoing(definition, sourceId, portId);
        var branch = NewId("branch");
        var whenTrue = NewId("true");
        var whenFalse = NewId("false");
        var merge = NewId("merge");
        var nodes = definition.Nodes.Add(branch, new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") })
            .Add(whenTrue, new(NodeTypes.Uppercase, "1.0.0")).Add(whenFalse, new(NodeTypes.Reverse, "1.0.0"))
            .Add(merge, new(NodeTypes.Merge, "1.0.0"));
        var connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(branch, "in"), "data"))
            .Add(NewId("link"), Link(branch, "true", whenTrue)).Add(NewId("link"), Link(branch, "false", whenFalse))
            .Add(NewId("link"), Link(whenTrue, "out", merge)).Add(NewId("link"), Link(whenFalse, "out", merge))
            .Add(NewId("link"), new(new(merge, "out"), edge.Value.Target, "data"));
        return Checked(definition with { SchemaVersion = definition.SchemaVersion is "1.8" or "1.9" ? definition.SchemaVersion : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : definition.SchemaVersion == "1.5" ? "1.5" : "1.4", Nodes = nodes, Connections = connections });
    }

    public static WorkflowDefinition InsertNodeAfter(WorkflowDefinition definition, string sourceId, string portId, NodeDefinition node)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (node.Type == NodeTypes.JsonSelectString)
            throw new ArgumentException("Use InsertJsonSelectorAfterInput to create the typed JSON input boundary.", nameof(node));
        RequireProcessing(node.Type);
        var edge = Outgoing(definition, sourceId, portId);
        var id = NewId("node");
        return Checked(definition with
        {
            SchemaVersion = PipelineEditing.SchemaFor(definition, node),
            Nodes = definition.Nodes.Add(id, node),
            Connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(id, "in"), "data"))
                .Add(NewId("link"), new(new(id, "out"), edge.Value.Target, "data"))
        });
    }

    public static WorkflowDefinition InsertJsonSelectorAfterInput(WorkflowDefinition definition, string inputId, string pointer = "/name")
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(inputId, out var input) || input.Type != NodeTypes.Input || input.InputDataType == "json")
            throw new ArgumentException("Choose the text workflow input to convert to JSON.", nameof(inputId));
        WorkflowJsonPointer.Parse(pointer);
        var edge = Outgoing(definition, inputId, "out");
        var selectorId = NewId("json-select");
        var selector = new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0")
        { JsonPropertySelection = new(pointer) };

        return Checked(definition with
        {
            SchemaVersion = "1.9",
            Nodes = definition.Nodes.SetItem(inputId, input with { InputDataType = "json" }).Add(selectorId, selector),
            Connections = definition.Connections.SetItem(edge.Key, new(new(selectorId, "out"), edge.Value.Target, "data"))
                .Add(NewId("link"), new(new(inputId, "out"), new(selectorId, "in"), "data"))
        });
    }

    public static WorkflowDefinition ReplaceNode(WorkflowDefinition definition, string id, NodeDefinition node)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var existing)) throw new ArgumentException("Unknown node.");
        if (existing.Type == node.Type && existing.Type is NodeTypes.Condition or NodeTypes.Approval)
            return Checked(definition with { Nodes = definition.Nodes.SetItem(id, node) });
        RequireProcessing(existing.Type);
        RequireProcessing(node.Type);
        return Checked(definition with { SchemaVersion = PipelineEditing.SchemaFor(definition, node), Nodes = definition.Nodes.SetItem(id, node) });
    }

    public static WorkflowDefinition RemoveNode(WorkflowDefinition definition, string id)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var node)) throw new ArgumentException("Unknown node.");
        if (node.Type == NodeTypes.JsonSelectString) return RemoveJsonSelector(definition, id);
        RequireProcessing(node.Type);
        var incoming = definition.Connections.Single(e => e.Value.Target.NodeId == id);
        var outgoing = definition.Connections.Single(e => e.Value.Source.NodeId == id);
        return Checked(definition with
        {
            Nodes = definition.Nodes.Remove(id),
            Connections = definition.Connections.Remove(outgoing.Key).SetItem(incoming.Key, new(incoming.Value.Source, outgoing.Value.Target, "data"))
        });
    }

    public static WorkflowDefinition RemoveJsonSelector(WorkflowDefinition definition, string id)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var selector) || selector.Type != NodeTypes.JsonSelectString)
            throw new ArgumentException("Choose a JSON selector node.", nameof(id));
        var incoming = definition.Connections.Single(edge => edge.Value.Target.NodeId == id);
        var outgoing = definition.Connections.Single(edge => edge.Value.Source.NodeId == id);
        var inputId = incoming.Value.Source.NodeId;
        if (!definition.Nodes.TryGetValue(inputId, out var input) || input.Type != NodeTypes.Input)
            throw new ArgumentException("The JSON selector is not connected directly to the workflow input.", nameof(id));

        return Checked(definition with
        {
            Nodes = definition.Nodes.Remove(id).SetItem(inputId, input with { InputDataType = null }),
            Connections = definition.Connections.Remove(incoming.Key).SetItem(outgoing.Key,
                new(incoming.Value.Source, outgoing.Value.Target, "data"))
        });
    }

    public static WorkflowDefinition InsertApprovalBefore(WorkflowDefinition definition, string targetId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(targetId, out var target) || target.Type != NodeTypes.McpTool)
            throw new ArgumentException("Choose an MCP tool node to guard.");
        var edge = definition.Connections.Single(e => e.Value.Target.NodeId == targetId);
        if (definition.Nodes[edge.Value.Source.NodeId].Type == NodeTypes.Approval) throw new ArgumentException("This tool already has an approval step.");
        var id = NewId("approval");
        return Checked(definition with
        {
            SchemaVersion = definition.SchemaVersion is "1.8" or "1.9" ? definition.SchemaVersion : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : "1.5",
            Nodes = definition.Nodes.Add(id, new(NodeTypes.Approval, "1.0.0") { Approval = new("Review the exact MCP call before allowing it.") }),
            Connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(id, "in"), "data"))
                .Add(NewId("link"), Link(id, "out", targetId))
        });
    }

    public static WorkflowDefinition RemoveApproval(WorkflowDefinition definition, string id)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var node) || node.Type != NodeTypes.Approval) throw new ArgumentException("Choose an approval step.");
        var incoming = definition.Connections.Single(e => e.Value.Target.NodeId == id);
        var outgoing = definition.Connections.Single(e => e.Value.Source.NodeId == id);
        return Checked(definition with
        {
            Nodes = definition.Nodes.Remove(id),
            Connections = definition.Connections.Remove(outgoing.Key).SetItem(incoming.Key, new(incoming.Value.Source, outgoing.Value.Target, "data"))
        });
    }

    private static WorkflowDefinition Checked(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition;
    }
    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
    private static ConnectionDefinition Link(string source, string port, string target) => new(new(source, port), new(target, "in"), "data");
    private static KeyValuePair<string, ConnectionDefinition> Outgoing(WorkflowDefinition definition, string id, string port) =>
        definition.Connections.SingleOrDefault(e => e.Value.Source.NodeId == id && e.Value.Source.PortId == port) is { Value: not null } edge
            ? edge : throw new ArgumentException("Choose a connected output port.");
    private static void RequireProcessing(string type)
    {
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString))
            throw new ArgumentException("Only processing nodes can be inserted, replaced or removed here.");
    }
}
