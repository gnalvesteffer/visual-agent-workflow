using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Validated local edge edits. No topological order is treated as an execution sequence.</summary>
public static class GraphEditing
{
    /// <summary>Upgrades the blank input-to-output graph to the first explicit execution/data schema.</summary>
    public static WorkflowDefinition EnableExplicitGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph && definition.ExecutionConnections is not null) return definition;
        var onlyProcessingNodes = definition.Nodes.Count == 2 &&
            definition.Nodes.TryGetValue(definition.EntryNodeId, out var input) && input.Type == NodeTypes.Input &&
            definition.Nodes.Values.Count(node => node.Type == NodeTypes.Output) == 1;
        var output = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (!onlyProcessingNodes || output.Value is null || definition.Connections.Count != 1 ||
            !definition.Connections.Values.Any(edge => edge.Source.NodeId == definition.EntryNodeId && edge.Source.PortId == "out" &&
                edge.Target.NodeId == output.Key && edge.Target.PortId == "in"))
            throw new ArgumentException("Only a blank Input-to-Output workflow can be upgraded to this explicit graph preview.", nameof(definition));

        var executionId = NewId("activate");
        var upgraded = definition with
        {
            SchemaVersion = WorkflowSchemaVersions.ExplicitGraph,
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(executionId, new(new(definition.EntryNodeId, "then"), new(output.Key, "execute"), "execution"))
        };
        WorkflowValidator.ThrowIfInvalid(upgraded);
        return upgraded;
    }

    /// <summary>Enables the bounded 1.24 branch preview without changing existing graph wires.</summary>
    public static WorkflowDefinition EnableExplicitBranchGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.ExplicitGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Start the explicit graph preview before enabling branch routing.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.ExplicitBranchGraph };
    }

    /// <summary>Upgrades a valid schema 1.24 branch preview to the bounded Boolean primitive subset.</summary>
    public static WorkflowDefinition EnableTypedBooleanGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.ExplicitBranchGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Start the Boolean Branch preview before enabling Boolean constants and NOT.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.TypedBooleanGraph };
    }

    /// <summary>Upgrades a valid typed-Boolean proof to the bounded schema 1.26 branch topology.</summary>
    public static WorkflowDefinition EnableBooleanTopologyGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.TypedBooleanGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable Boolean primitives before expanding the branch topology.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.BooleanTopologyGraph };
    }

    /// <summary>Upgrades a valid schema 1.26 topology to support embedded pure graph functions.</summary>
    public static WorkflowDefinition EnablePureGraphFunctions(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.BooleanTopologyGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable bounded Boolean branch topologies before adding pure graph functions.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.PureGraphFunctions };
    }

    /// <summary>Adds a configured node without connecting it. Incomplete topology remains an editable draft.</summary>
    public static WorkflowDefinition AddUnconnectedNode(WorkflowDefinition definition, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(node);
        if (definition.Nodes.Count >= 256) throw new ArgumentException("A workflow can contain at most 256 nodes.", nameof(definition));
        var prefix = node.Type switch
        {
            NodeTypes.Condition => "condition", NodeTypes.Terminate => "terminate", NodeTypes.Merge => "merge",
            NodeTypes.Agent => "agent", NodeTypes.McpTool => "mcp", NodeTypes.StringConstant => "constant", NodeTypes.TextConcat => "concat",
            NodeTypes.StringEquals => "equals", NodeTypes.BooleanBranch => "branch",
            NodeTypes.BooleanConstant => "boolean", NodeTypes.BooleanNot => "not", _ => node.Type
        };
        var id = NewId(prefix);
        return definition with
        {
            SchemaVersion = PipelineEditing.SchemaFor(definition, node),
            Nodes = definition.Nodes.Add(id, node)
        };
    }

    /// <summary>Reconnects pins in an editable draft. Structural validation remains visible until every issue is fixed.</summary>
    public static WorkflowDefinition ConnectPins(WorkflowDefinition definition, string sourceId, string sourcePortId, string targetId, string targetPortId)
    {
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition.SchemaVersion))
            return ConnectExplicitPins(definition, sourceId, sourcePortId, targetId, targetPortId);

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
            SchemaVersion = output.DataType == "json" && definition.SchemaVersion is not ("1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16") ? "1.13" : definition.SchemaVersion,
            Connections = connections.Add(connectionId, new(new(sourceId, sourcePortId), new(targetId, targetPortId), "data"))
        };
    }

    public static WorkflowDefinition Disconnect(WorkflowDefinition definition, string connectionId)
    {
        if (connectionId.StartsWith("execution:", StringComparison.Ordinal))
        {
            var executionId = connectionId["execution:".Length..];
            if (definition.ExecutionConnections?.ContainsKey(executionId) != true) throw new ArgumentException("The execution connection no longer exists.", nameof(connectionId));
            return definition with { ExecutionConnections = definition.ExecutionConnections.Remove(executionId) };
        }
        else if (connectionId.StartsWith("data:", StringComparison.Ordinal))
        {
            var dataId = connectionId["data:".Length..];
            if (!definition.Connections.ContainsKey(dataId)) throw new ArgumentException("The data connection no longer exists.", nameof(connectionId));
            return definition with { Connections = definition.Connections.Remove(dataId) };
        }
        if (definition.ExecutionConnections?.ContainsKey(connectionId) == true)
            return definition with { ExecutionConnections = definition.ExecutionConnections.Remove(connectionId) };
        if (!definition.Connections.ContainsKey(connectionId)) throw new ArgumentException("The connection no longer exists.", nameof(connectionId));
        return definition with { Connections = definition.Connections.Remove(connectionId) };
    }

    private static WorkflowDefinition ConnectExplicitPins(WorkflowDefinition definition, string sourceId, string sourcePortId, string targetId, string targetPortId)
    {
        if (!definition.Nodes.TryGetValue(sourceId, out var source) || !definition.Nodes.TryGetValue(targetId, out var target))
            throw new ArgumentException("Choose pins on existing nodes.");
        var sourcePort = WorkflowPorts.Explicit(source).SingleOrDefault(port => port.Direction == WorkflowPortDirection.Output && port.Id == sourcePortId);
        var targetPort = WorkflowPorts.Explicit(target).SingleOrDefault(port => port.Direction == WorkflowPortDirection.Input && port.Id == targetPortId);
        if (sourcePort is null || targetPort is null || sourcePort.Kind != targetPort.Kind || sourcePort.DataType != targetPort.DataType)
            throw new ArgumentException("Connect pins with the same kind and compatible data type.");

        var connectionId = NewId("link");
        var connection = new ConnectionDefinition(new(sourceId, sourcePortId), new(targetId, targetPortId),
            sourcePort.Kind == WorkflowPortKind.Execution ? "execution" : "data");
        if (sourcePort.Kind == WorkflowPortKind.Execution)
        {
            var edges = definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal);
            var allowsAlternativeInput = definition.SchemaVersion is WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions &&
                target.Type is NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate;
            // Schema 1.26 treats incoming execution edges as mutually exclusive alternatives.
            // Each source pin still chooses only one destination; earlier schemas keep one edge per input.
            edges = edges.RemoveRange(edges.Where(edge => !allowsAlternativeInput && edge.Value.Target.NodeId == targetId && edge.Value.Target.PortId == targetPortId ||
                edge.Value.Source.NodeId == sourceId && edge.Value.Source.PortId == sourcePortId).Select(edge => edge.Key));
            return definition with { ExecutionConnections = edges.Add(connectionId, connection) };
        }

        var data = definition.Connections.RemoveRange(definition.Connections
            .Where(edge => edge.Value.Target.NodeId == targetId && edge.Value.Target.PortId == targetPortId)
            .Select(edge => edge.Key));
        return definition with { Connections = data.Add(connectionId, connection) };
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
        return Checked(definition with { SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" or "1.15" or "1.14" or "1.13" or "1.12" or "1.11" or "1.10" ? definition.SchemaVersion : definition.SchemaVersion == "1.9" ? "1.9" : definition.SchemaVersion == "1.8" ? "1.8" : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : definition.SchemaVersion == "1.5" ? "1.5" : "1.4", Nodes = nodes, Connections = connections });
    }

    public static WorkflowDefinition InsertParallelBlockAfter(WorkflowDefinition definition, string sourceId, string portId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var existingBlockCount = definition.Nodes.Values.Count(node => node.Type == NodeTypes.ParallelFork);
        if (existingBlockCount >= ParallelGraphLimits.MaximumBlocks)
            throw new ArgumentException($"A workflow can contain at most {ParallelGraphLimits.MaximumBlocks} sequential parallel blocks.", nameof(definition));
        var edge = Outgoing(definition, sourceId, portId);
        var source = definition.Nodes[sourceId];
        if (WorkflowPorts.Outputs(source).Single(port => port.Id == portId).DataType != "string")
            throw new ArgumentException("Parallel blocks currently accept string values.", nameof(portId));
        var target = definition.Nodes[edge.Value.Target.NodeId];
        var targetPort = WorkflowPorts.Inputs(target).Single(port => port.Id == edge.Value.Target.PortId);
        var needsTextSelector = targetPort.DataType == "string" && target.Type != NodeTypes.Output;
        var addedCount = needsTextSelector ? 5 : 4;
        if (definition.Nodes.Count + addedCount > 256)
            throw new ArgumentException("The parallel block would exceed the 256-node workflow limit.");

        var forkId = NewId("parallel-fork");
        var firstBranchId = NewId("parallel-branch");
        var secondBranchId = NewId("parallel-branch");
        var joinId = NewId("parallel-join");
        var selectorId = needsTextSelector ? NewId("json-select") : null;
        var nodes = definition.Nodes
            .Add(forkId, new(NodeTypes.ParallelFork, "1.0.0"))
            .Add(firstBranchId, new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add(secondBranchId, new(NodeTypes.ParallelBranch, "1.0.0"))
            .Add(joinId, new(NodeTypes.ParallelJoin, "1.0.0"));
        if (selectorId is not null)
            nodes = nodes.Add(selectorId, new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new("") });
        if (target.Type == NodeTypes.Output && !needsTextSelector)
            nodes = nodes.SetItem(edge.Value.Target.NodeId, target with { OutputDataType = "json" });

        var connections = definition.Connections
            .SetItem(edge.Key, new(edge.Value.Source, new(forkId, "in"), "data"))
            .Add(NewId("link"), new(new(forkId, "branch_1"), new(firstBranchId, "in"), "data"))
            .Add(NewId("link"), new(new(forkId, "branch_2"), new(secondBranchId, "in"), "data"))
            .Add(NewId("link"), new(new(firstBranchId, "out"), new(joinId, "branch_1"), "data"))
            .Add(NewId("link"), new(new(secondBranchId, "out"), new(joinId, "branch_2"), "data"));
        connections = selectorId is null
            ? connections.Add(NewId("link"), new(new(joinId, "out"), edge.Value.Target, "data"))
            : connections.Add(NewId("link"), new(new(joinId, "out"), new(selectorId, "in"), "data"))
                .Add(NewId("link"), new(new(selectorId, "out"), edge.Value.Target, "data"));

        var schemaVersion = existingBlockCount > 0 ? definition.SchemaVersion is "1.22" or "1.21" ? definition.SchemaVersion : "1.20" : definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16"
            ? definition.SchemaVersion : definition.SchemaVersion == "1.15" ? "1.15" : "1.14";
        return Checked(definition with { SchemaVersion = schemaVersion, Nodes = nodes, Connections = connections });
    }

    public static WorkflowDefinition InsertBoundedLoopAfter(WorkflowDefinition definition, string sourceId, string portId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.Nodes.Values.Any(node => node.Type is NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Approval or
            NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.Loop) ||
            WorkflowPorts.Outputs(definition.Nodes[sourceId]).SingleOrDefault(port => port.Id == portId)?.DataType != "string")
            throw new ArgumentException("A bounded loop can be added once to a linear string path.", nameof(sourceId));
        if (definition.Nodes.Count > 254) throw new ArgumentException("The loop would exceed the 256-node workflow limit.");

        var edge = Outgoing(definition, sourceId, portId);
        var loopId = NewId("loop");
        var bodyId = NewId("loop-body");
        var loop = new NodeDefinition(NodeTypes.Loop, "1.0.0")
        { Loop = new(new("contains", "continue"), 3) };
        return Checked(definition with
        {
            SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" or "1.20" ? definition.SchemaVersion : definition.SchemaVersion == "1.19" ? "1.19" : definition.SchemaVersion == "1.18" ? "1.18" : "1.17",
            Nodes = definition.Nodes.Add(loopId, loop).Add(bodyId, new(NodeTypes.LoopBody, "1.0.0")),
            Connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(loopId, "in"), "data"))
                .Add(NewId("link"), Link(loopId, "body", bodyId))
                .Add(NewId("link"), Link(bodyId, "out", loopId))
                .Add(NewId("link"), new(new(loopId, "done"), edge.Value.Target, "data"))
        });
    }

    public static WorkflowDefinition InsertTerminationAfter(WorkflowDefinition definition, string sourceId, string portId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var edge = Outgoing(definition, sourceId, portId);
        var sourcePort = WorkflowPorts.Outputs(definition.Nodes[sourceId]).Single(port => port.Id == portId);
        var target = definition.Nodes[edge.Value.Target.NodeId];
        var targetPort = WorkflowPorts.Inputs(target).Single(port => port.Id == edge.Value.Target.PortId);
        if (sourcePort.DataType != "string" || targetPort.DataType != "string")
            throw new ArgumentException("Termination exits currently accept string paths.", nameof(portId));
        if (definition.Nodes.Values.Count(node => node.Type == NodeTypes.Terminate) >= 16)
            throw new ArgumentException("A workflow can have at most 16 termination exits.", nameof(definition));
        if (definition.Nodes.Count >= 256)
            throw new ArgumentException("The termination node would exceed the 256-node workflow limit.", nameof(definition));

        var id = NewId("terminate");
        var result = definition with
        {
            SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" or "1.20" ? definition.SchemaVersion : definition.SchemaVersion == "1.19" ? "1.19" : "1.18",
            Nodes = definition.Nodes.Add(id, new(NodeTypes.Terminate, "1.0.0")),
            Connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(id, "in"), "data"))
        };
        return Checked(result);
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
            SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" or "1.15" or "1.14" or "1.13" or "1.12" or "1.11" or "1.10" ? definition.SchemaVersion : "1.9",
            Nodes = definition.Nodes.SetItem(inputId, input with { InputDataType = "json" }).Add(selectorId, selector),
            Connections = definition.Connections.SetItem(edge.Key, new(new(selectorId, "out"), edge.Value.Target, "data"))
                .Add(NewId("link"), new(new(inputId, "out"), new(selectorId, "in"), "data"))
        });
    }

    public static WorkflowDefinition InsertJsonSelectorAfterJson(WorkflowDefinition definition, string sourceId, string portId, string pointer = "")
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(sourceId, out var source) ||
            WorkflowPorts.Outputs(source).SingleOrDefault(port => port.Id == portId)?.DataType != "json")
            throw new ArgumentException("Choose a JSON output pin.", nameof(portId));
        WorkflowJsonPointer.Parse(pointer);
        var edge = Outgoing(definition, sourceId, portId);
        var selectorId = NewId("json-select");
        var target = definition.Nodes[edge.Value.Target.NodeId];
        var nodes = definition.Nodes.Add(selectorId,
            new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new(pointer) });
        if (target.Type == NodeTypes.Output) nodes = nodes.SetItem(edge.Value.Target.NodeId, target with { OutputDataType = null });
        var connections = definition.Connections.SetItem(edge.Key, new(edge.Value.Source, new(selectorId, "in"), "data"))
            .Add(NewId("link"), new(new(selectorId, "out"), edge.Value.Target, "data"));
        return Checked(definition with { Nodes = nodes, Connections = connections });
    }

    public static WorkflowDefinition ReplaceNode(WorkflowDefinition definition, string id, NodeDefinition node)
    {
        if (!WorkflowDraftEditing.HasOnlyIncompleteTopologyIssues(definition)) WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var existing)) throw new ArgumentException("Unknown node.");
        if (existing.Type == node.Type && existing.Type is NodeTypes.Condition or NodeTypes.Approval or NodeTypes.Loop or NodeTypes.StringConstant or NodeTypes.BooleanConstant)
            return WorkflowDraftEditing.ValidateEdit(definition, definition with { Nodes = definition.Nodes.SetItem(id, node) });
        RequireProcessing(existing.Type);
        RequireProcessing(node.Type);
        return WorkflowDraftEditing.ValidateEdit(definition,
            definition with { SchemaVersion = PipelineEditing.SchemaFor(definition, node), Nodes = definition.Nodes.SetItem(id, node) });
    }

    public static WorkflowDefinition RemoveNode(WorkflowDefinition definition, string id)
    {
        if (!definition.Nodes.TryGetValue(id, out var requestedNode)) throw new ArgumentException("Unknown node.", nameof(id));
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition.SchemaVersion))
        {
            if (requestedNode.Type is NodeTypes.Input or NodeTypes.Output)
                throw new ArgumentException("Input and output boundary nodes cannot be removed independently.", nameof(id));
            return definition with
            {
                Nodes = definition.Nodes.Remove(id),
                Connections = definition.Connections.RemoveRange(definition.Connections
                    .Where(edge => edge.Value.Source.NodeId == id || edge.Value.Target.NodeId == id)
                    .Select(edge => edge.Key)),
                ExecutionConnections = definition.ExecutionConnections?.RemoveRange(definition.ExecutionConnections
                    .Where(edge => edge.Value.Source.NodeId == id || edge.Value.Target.NodeId == id)
                    .Select(edge => edge.Key))
            };
        }
        if (!WorkflowValidator.Validate(definition).IsEmpty)
        {
            if (requestedNode.Type is NodeTypes.Input or NodeTypes.Output or NodeTypes.LoopBody)
                throw new ArgumentException("Input, output, and loop-body boundary nodes cannot be removed independently.", nameof(id));
            var removedIds = new HashSet<string>(StringComparer.Ordinal) { id };
            if (requestedNode.Type == NodeTypes.Loop)
                foreach (var edge in definition.Connections.Values.Where(edge => edge.Source.NodeId == id && edge.Source.PortId == "body"))
                    if (definition.Nodes.TryGetValue(edge.Target.NodeId, out var body) && body.Type == NodeTypes.LoopBody) removedIds.Add(edge.Target.NodeId);
            return definition with
            {
                Nodes = definition.Nodes.RemoveRange(removedIds),
                Connections = definition.Connections.RemoveRange(definition.Connections
                    .Where(edge => removedIds.Contains(edge.Value.Source.NodeId) || removedIds.Contains(edge.Value.Target.NodeId))
                    .Select(edge => edge.Key))
            };
        }
        if (definition.Nodes.TryGetValue(id, out var loop) && loop.Type == NodeTypes.Loop)
            return RemoveBoundedLoop(definition, id);
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
        if (node.Type == NodeTypes.LoopBody) throw new ArgumentException("The loop-body anchor is required by its loop.", nameof(id));
        if (node.Type == NodeTypes.JsonSelectString) return RemoveJsonSelector(definition, id);
        if (node.Type == NodeTypes.Terminate)
            return definition with
            {
                Nodes = definition.Nodes.Remove(id),
                Connections = definition.Connections.RemoveRange(definition.Connections
                    .Where(edge => edge.Value.Source.NodeId == id || edge.Value.Target.NodeId == id)
                    .Select(edge => edge.Key))
            };
        RequireProcessing(node.Type);
        var incoming = definition.Connections.Single(e => e.Value.Target.NodeId == id);
        var outgoing = definition.Connections.Single(e => e.Value.Source.NodeId == id);
        return Checked(definition with
        {
            Nodes = definition.Nodes.Remove(id),
            Connections = definition.Connections.Remove(outgoing.Key).SetItem(incoming.Key, new(incoming.Value.Source, outgoing.Value.Target, "data"))
        });
    }

    private static WorkflowDefinition RemoveBoundedLoop(WorkflowDefinition definition, string loopId)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(loopId, out var loop) || loop.Type != NodeTypes.Loop ||
            WorkflowGraph.LoopBackConnectionId(definition) is not { } backId)
            throw new ArgumentException("Choose a valid bounded loop.", nameof(loopId));
        var incoming = definition.Connections.Where(edge => edge.Value.Target.NodeId == loopId).ToArray();
        var initial = incoming.Single(edge => edge.Key != backId);
        var bodyStart = definition.Connections.Single(edge => edge.Value.Source.NodeId == loopId && edge.Value.Source.PortId == "body");
        var anchorId = bodyStart.Value.Target.NodeId;
        var firstBody = definition.Connections.Single(edge => edge.Value.Source.NodeId == anchorId);
        var bodyTail = definition.Connections[backId];
        var done = definition.Connections.Single(edge => edge.Value.Source.NodeId == loopId && edge.Value.Source.PortId == "done");
        var removedIds = new HashSet<string>([loopId, anchorId], StringComparer.Ordinal);
        var connections = definition.Connections.RemoveRange(definition.Connections
            .Where(edge => removedIds.Contains(edge.Value.Source.NodeId) || removedIds.Contains(edge.Value.Target.NodeId) || edge.Key == backId)
            .Select(edge => edge.Key));
        if (firstBody.Value.Target.NodeId == loopId)
            connections = connections.Add(NewId("link"), new(initial.Value.Source, done.Value.Target, "data"));
        else
            connections = connections
                .Add(NewId("link"), new(initial.Value.Source, new(firstBody.Value.Target.NodeId, firstBody.Value.Target.PortId), "data"))
                .Add(NewId("link"), new(new(bodyTail.Source.NodeId, bodyTail.Source.PortId), done.Value.Target, "data"));
        return Checked(definition with
        {
            Nodes = definition.Nodes.Remove(loopId).Remove(anchorId),
            Connections = connections
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
            SchemaVersion = sourceType == "json" && definition.SchemaVersion is not ("1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16") ? "1.13" : definition.SchemaVersion,
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
            SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" or "1.20" or "1.19" or "1.18" or "1.17" or "1.16" or "1.15" or "1.14" or "1.13" or "1.12" or "1.11" or "1.10" ? definition.SchemaVersion : definition.SchemaVersion == "1.9" ? "1.9" : definition.SchemaVersion == "1.8" ? "1.8" : definition.SchemaVersion == "1.7" ? "1.7" : definition.SchemaVersion == "1.6" ? "1.6" : "1.5",
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
        if (type is not (NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.TextTemplate or NodeTypes.WorkflowCall))
            throw new ArgumentException("Only processing nodes can be inserted, replaced or removed here.");
    }
}
