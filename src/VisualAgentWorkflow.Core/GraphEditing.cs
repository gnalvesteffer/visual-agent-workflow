namespace VisualAgentWorkflow.Core;

/// <summary>Validated local edge edits. No topological order is treated as an execution sequence.</summary>
public static class GraphEditing
{
    /// <summary>Reconnects pins in an editable draft. Structural validation remains visible until every issue is fixed.</summary>
    public static WorkflowDefinition ConnectPins(WorkflowDefinition definition, string sourceId, string sourcePortId, string targetId, string targetPortId)
    {
        if (!definition.Nodes.TryGetValue(sourceId, out var source) || !definition.Nodes.TryGetValue(targetId, out var target))
            throw new ArgumentException("Choose pins on existing nodes.");
        var output = WorkflowPorts.Outputs(source).SingleOrDefault(port => port.Id == sourcePortId);
        var input = target.Type == NodeTypes.Output && targetPortId == "in"
            ? new WorkflowPort("in", output?.DataType ?? "")
            : WorkflowPorts.Inputs(target).SingleOrDefault(port => port.Id == targetPortId);
        if (output is null || input is null || output.DataType != input.DataType)
            throw new ArgumentException("Connect an output pin to an input pin with the same data type.");
        if (sourceId == targetId) throw new ArgumentException("A node cannot connect to itself.");

        var connections = definition.Connections
            .RemoveRange(definition.Connections.Where(edge => target.Type is not (NodeTypes.Merge or NodeTypes.Output) && edge.Value.Target.NodeId == targetId && edge.Value.Target.PortId == targetPortId ||
                edge.Value.Source.NodeId == sourceId && edge.Value.Source.PortId == sourcePortId).Select(edge => edge.Key));
        var connectionId = "link-" + Guid.NewGuid().ToString("N");
        return definition with
        {
            Nodes = target.Type == NodeTypes.Output
                ? definition.Nodes.SetItem(targetId, target with { OutputDataType = output.DataType == "json" ? "json" : null })
                : definition.Nodes,
            SchemaVersion = output.DataType == "json" ? "1.13" : definition.SchemaVersion,
            Connections = connections.Add(connectionId, new(new(sourceId, sourcePortId), new(targetId, targetPortId), "data"))
        };
    }

    public static WorkflowDefinition Disconnect(WorkflowDefinition definition, string connectionId)
    {
        if (!definition.Connections.ContainsKey(connectionId)) throw new ArgumentException("The connection no longer exists.", nameof(connectionId));
        return definition with { Connections = definition.Connections.Remove(connectionId) };
    }

    public static WorkflowDefinition InsertConditionalAfter(WorkflowDefinition definition, string sourceId, string portId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var edge = Outgoing(definition, sourceId, portId);
        var branch = NewId("branch");
        var merge = NewId("merge");
        var nodes = definition.Nodes.Add(branch, new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") })
            .Add(merge, new(NodeTypes.Merge, "1.0.0"));
        var connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(branch, "in"), "data"))
            .Add(NewId("link"), Link(branch, "true", merge)).Add(NewId("link"), Link(branch, "false", merge))
            .Add(NewId("link"), new(new(merge, "out"), edge.Value.Target, "data"));
        return Checked(definition with { SchemaVersion = definition.SchemaVersion is "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" ? definition.SchemaVersion : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : definition.SchemaVersion == "1.5" ? "1.5" : "1.4", Nodes = nodes, Connections = connections });
    }

    public static WorkflowDefinition InsertNodeAfter(WorkflowDefinition definition, string sourceId, string portId, NodeDefinition node)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (node.Type == NodeTypes.JsonSelectString)
            throw new ArgumentException("Use InsertJsonSelectorAfterInput to create the typed JSON input boundary.", nameof(node));
        RequireProcessing(node.Type);
        var edge = Outgoing(definition, sourceId, portId);
        var id = NewId("node");
        var outputType = WorkflowPorts.Outputs(node).Single().DataType;
        var nodes = definition.Nodes.Add(id, node);
        if (definition.Nodes[edge.Value.Target.NodeId].Type == NodeTypes.Output)
            nodes = nodes.SetItem(edge.Value.Target.NodeId,
                nodes[edge.Value.Target.NodeId] with { OutputDataType = outputType == "json" ? "json" : null });
        return Checked(definition with
        {
            SchemaVersion = PipelineEditing.SchemaFor(definition, node),
            Nodes = nodes,
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
            SchemaVersion = definition.SchemaVersion is "1.13" or "1.12" or "1.11" or "1.10" ? definition.SchemaVersion : "1.9",
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
        if (definition.Nodes.TryGetValue(id, out var merge) && merge.Type == NodeTypes.Merge)
        {
            // Removing a merge intentionally leaves its former routes disconnected so the
            // user can assign each condition arm its own destination in the draft editor.
            return definition with
            {
                Nodes = definition.Nodes.Remove(id),
                Connections = definition.Connections.RemoveRange(definition.Connections
                    .Where(edge => edge.Value.Source.NodeId == id || edge.Value.Target.NodeId == id)
                    .Select(edge => edge.Key))
            };
        }
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
        var sourceId = incoming.Value.Source.NodeId;
        var source = definition.Nodes[sourceId];
        var sourceType = WorkflowPorts.Outputs(source).Single(port => port.Id == incoming.Value.Source.PortId).DataType;
        var target = definition.Nodes[outgoing.Value.Target.NodeId];
        var nodes = definition.Nodes.Remove(id);
        if (source.Type == NodeTypes.Input && target.Type != NodeTypes.Output && sourceType == "json" &&
            WorkflowPorts.Inputs(target).Single(port => port.Id == outgoing.Value.Target.PortId).DataType == "string")
        {
            source = source with { InputDataType = null };
            nodes = nodes.SetItem(sourceId, source);
            sourceType = "string";
        }
        if (target.Type == NodeTypes.Output)
            nodes = nodes.SetItem(outgoing.Value.Target.NodeId, target with { OutputDataType = sourceType == "json" ? "json" : null });

        return Checked(definition with
        {
            SchemaVersion = sourceType == "json" ? "1.13" : definition.SchemaVersion,
            Nodes = nodes,
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
            SchemaVersion = definition.SchemaVersion is "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" ? definition.SchemaVersion : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : "1.5",
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
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.TextTemplate))
            throw new ArgumentException("Only processing nodes can be inserted, replaced or removed here.");
    }
}
