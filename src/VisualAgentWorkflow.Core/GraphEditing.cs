using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowNodeDuplicationResult(WorkflowDefinition Definition, string NodeId);

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

    /// <summary>Creates the first bounded run-scoped variable graph from a valid blank explicit graph.</summary>
    public static WorkflowDefinition EnableMutableVariablesGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.ExplicitGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Start with a valid typed graph before enabling run-scoped variables.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        var input = definition.Nodes[definition.EntryNodeId];
        var outputEntry = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output);
        if (definition.Nodes.Count != 2 || definition.Connections.Count != 1 ||
            !definition.Connections.Values.Any(edge => edge.Source == new PortReference(definition.EntryNodeId, "out") &&
                edge.Target == new PortReference(outputEntry.Key, "in")) ||
            definition.ExecutionConnections.Count != 1 ||
            !definition.ExecutionConnections.Values.Any(edge => edge.Source == new PortReference(definition.EntryNodeId, "then") &&
                edge.Target == new PortReference(outputEntry.Key, "execute")))
            throw new ArgumentException("Only a blank Input-to-Output typed graph can be upgraded to the first variable proof.", nameof(definition));

        var setId = NewId("variable-set");
        var getId = NewId("variable-get");
        var variableId = "value";
        var nodes = definition.Nodes
            .Add(setId, new(NodeTypes.VariableSet, "1.0.0") { VariableId = variableId, InputDataType = WorkflowDataTypes.String })
            .Add(getId, new(NodeTypes.VariableGet, "1.0.0") { VariableId = variableId, OutputDataType = WorkflowDataTypes.String })
            .SetItem(outputEntry.Key, outputEntry.Value with { OutputDataType = WorkflowDataTypes.String });
        var variables = (definition.Variables ?? ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty)
            .SetItem(variableId, new("Value", ""));
        var data = definition.Connections
            .SetItem(NewId("link"), new(new(getId, "out"), new(outputEntry.Key, "in"), "data"))
            .SetItem(NewId("link"), new(new(definition.EntryNodeId, "out"), new(setId, "value"), "data"))
            .Remove(definition.Connections.Keys.Single());
        var execution = definition.ExecutionConnections
            .Remove(definition.ExecutionConnections.Keys.Single())
            .SetItem(NewId("activate"), new(new(definition.EntryNodeId, "then"), new(setId, "execute"), "execution"))
            .SetItem(NewId("activate"), new(new(setId, "then"), new(getId, "execute"), "execution"))
            .SetItem(NewId("activate"), new(new(getId, "then"), new(outputEntry.Key, "execute"), "execution"));
        return Checked(definition with
        {
            SchemaVersion = WorkflowSchemaVersions.MutableVariablesGraph,
            Nodes = nodes,
            Connections = data,
            ExecutionConnections = execution,
            Variables = variables
        });
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

    /// <summary>Upgrades a valid linear explicit graph to the direct MCP tool schema.</summary>
    public static WorkflowDefinition EnableDirectMcpGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.ExplicitGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Start with a valid typed graph before enabling direct MCP tool nodes.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        var upgraded = definition with { SchemaVersion = WorkflowSchemaVersions.DirectMcpPinsGraph };
        WorkflowValidator.ThrowIfInvalid(upgraded);
        return upgraded;
    }

    /// <summary>Upgrades a valid direct-MCP pin graph to the finite-number pin schema.</summary>
    public static WorkflowDefinition EnableNumberGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.DirectMcpPinsGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable direct MCP tools before adding typed number pins.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        var upgraded = definition with { SchemaVersion = WorkflowSchemaVersions.NumberGraph };
        WorkflowValidator.ThrowIfInvalid(upgraded);
        return upgraded;
    }

    /// <summary>Promotes a number-pin graph so workflow number parameters can feed its persisted pins.</summary>
    public static WorkflowDefinition EnableNumberPropagationGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.NumberGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable finite number MCP pins before binding workflow number parameters.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        var upgraded = definition with { SchemaVersion = WorkflowSchemaVersions.NumberPropagationGraph };
        WorkflowValidator.ThrowIfInvalid(upgraded);
        return upgraded;
    }

    /// <summary>Promotes a number-propagation graph so it can use pure finite-number arithmetic.</summary>
    public static WorkflowDefinition EnableNumberArithmeticGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.NumberPropagationGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable finite-number propagation before adding number arithmetic nodes.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        var upgraded = definition with { SchemaVersion = WorkflowSchemaVersions.NumberArithmeticGraph };
        WorkflowValidator.ThrowIfInvalid(upgraded);
        return upgraded;
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

    /// <summary>Enables scheduled Agent nodes and their explicitly pinned pure graph-function tool revisions.</summary>
    public static WorkflowDefinition EnableAgentGraphFunctionTools(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.PureGraphFunctions || definition.ExecutionConnections is null)
            throw new ArgumentException("Only a valid schema 1.27 pure-function graph can be upgraded to agent graph-function tools.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.AgentGraphFunctionTools };
    }

    /// <summary>Upgrades a valid agent graph to typed Int64 graph values and arithmetic.</summary>
    public static WorkflowDefinition EnableIntegerGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.AgentExecutionRoutes || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable the agent graph schema before adding typed integer values.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.IntegerGraph };
    }

    /// <summary>Upgrades a valid integer graph to the strict explicit string-to-integer conversion schema.</summary>
    public static WorkflowDefinition EnableStringIntegerConversionGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.IntegerGraph || definition.ExecutionConnections is null)
            throw new ArgumentException("Enable typed integer graph values before adding explicit conversions.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition with { SchemaVersion = WorkflowSchemaVersions.StringIntegerConversionGraph };
    }

    /// <summary>
    /// Inserts the existing explicit string-to-integer conversion between an
    /// incompatible data pair. This is opt-in authoring, not implicit coercion.
    /// </summary>
    public static WorkflowDefinition InsertStringIntegerConversion(
        WorkflowDefinition definition, string sourceId, string sourcePortId, string targetId, string targetPortId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.StringIntegerConversionGraph ||
            definition.ExecutionConnections is null)
            throw new ArgumentException("String-to-integer insertion requires an explicit schema 1.39 conversion graph.", nameof(definition));
        if (sourceId == targetId || !definition.Nodes.TryGetValue(sourceId, out var source) ||
            !definition.Nodes.TryGetValue(targetId, out var target))
            throw new ArgumentException("Choose two existing nodes for the conversion.", nameof(sourceId));

        var sourcePort = WorkflowPorts.Explicit(source).SingleOrDefault(port =>
            port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data && port.Id == sourcePortId);
        var targetPort = WorkflowPorts.Explicit(target).SingleOrDefault(port =>
            port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data && port.Id == targetPortId);
        if (sourcePort?.DataType != WorkflowDataTypes.String || targetPort?.DataType != WorkflowDataTypes.Integer)
            throw new ArgumentException("Only a string output to an integer input can use this explicit conversion.", nameof(targetPortId));
        if (definition.Nodes.Count >= 256)
            throw new ArgumentException("The conversion node would exceed the 256-node workflow limit.", nameof(definition));
        if (definition.Connections.Values.Any(edge => edge.Target == new PortReference(targetId, targetPortId)))
            throw new ArgumentException("Disconnect the target input before inserting an explicit conversion.", nameof(targetPortId));
        if (definition.Connections.Values.Any(edge => edge.Source == new PortReference(sourceId, sourcePortId)))
            throw new ArgumentException("Disconnect the source output before inserting an explicit conversion.", nameof(sourcePortId));

        var conversionId = NewId("convert");
        var connections = definition.Connections
            .Add(NewId("link"), new(new(sourceId, sourcePortId), new(conversionId, "in"), "data"))
            .Add(NewId("link"), new(new(conversionId, "out"), new(targetId, targetPortId), "data"));

        return Checked(definition with
        {
            SchemaVersion = definition.SchemaVersion,
            Nodes = definition.Nodes.Add(conversionId, new(NodeTypes.StringToInteger, "1.0.0")),
            Connections = connections
        });
    }

    /// <summary>Inserts a named configurable parallel fan-out and barrier after the workflow input.</summary>
    public static WorkflowDefinition CreateConfigurableParallelGraph(WorkflowDefinition definition, int branchCount)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.IntegerGraph || branchCount is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches)
            throw new ArgumentException($"Choose {ParallelBranchLimits.MinimumBranches}–{ParallelGraphLimits.MaximumExplicitBranches} branches on a schema 1.31 integer graph.", nameof(branchCount));
        WorkflowValidator.ThrowIfInvalid(definition);

        var input = definition.Nodes.GetValueOrDefault(definition.EntryNodeId);
        var outputEntry = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (input?.Type != NodeTypes.Input || outputEntry.Value is null ||
            definition.ExecutionConnections is not { } executionConnections || definition.Nodes.Count > 256 - branchCount - 2)
            throw new ArgumentException("Create a configurable parallel region on a valid integer graph.", nameof(definition));
        var entryRoute = executionConnections.SingleOrDefault(edge => edge.Value.Source.NodeId == definition.EntryNodeId);
        if (entryRoute.Value is null || !definition.Nodes.TryGetValue(entryRoute.Value.Target.NodeId, out var firstNode) ||
            firstNode.Type is not (NodeTypes.Agent or NodeTypes.BooleanBranch))
            throw new ArgumentException("The workflow input must activate an Agent or Boolean Branch before adding the parallel region.", nameof(definition));
        var outputData = definition.Connections.SingleOrDefault(edge => edge.Value.Target.NodeId == outputEntry.Key && edge.Value.Target.PortId == "in");
        if (outputData.Value is null || outputData.Value.Source != new PortReference(definition.EntryNodeId, "out"))
            throw new ArgumentException("The workflow Output must still use the input text as its result before adding the parallel region.", nameof(definition));

        var forkId = NewId("parallel-fork");
        var joinId = NewId("parallel-join");
        var branches = ImmutableArray.CreateBuilder<ParallelBranchDescriptor>(branchCount);
        var nodes = definition.Nodes.Add(forkId, new(NodeTypes.ParallelFork, "1.0.0"));
        var execution = executionConnections.Remove(entryRoute.Key);

        for (var index = 1; index <= branchCount; index++)
        {
            var branchId = $"branch_{index}";
            var anchorId = NewId("parallel-branch");
            branches.Add(new(branchId, $"Branch {index}", anchorId));
            nodes = nodes.Add(anchorId, new(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new(forkId, branchId)
            });
            execution = execution
                .Add(NewId("link"), new(new(forkId, ParallelBranchLimits.PinId(branchId)), new(anchorId, "execute"), "execution"))
                .Add(NewId("link"), new(new(anchorId, "then"), new(joinId, ParallelBranchLimits.PinId(branchId)), "execution"));
        }

        var branchDescriptors = branches.ToImmutable();
        nodes = nodes
            .Add(joinId, new(NodeTypes.ParallelJoin, "1.0.0")
            {
                ParallelJoin = new(forkId, [.. branchDescriptors.Select(branch => branch.Id)])
            })
            .SetItem(outputEntry.Key, outputEntry.Value with { OutputDataType = "json" });
        execution = execution
            .Add(NewId("link"), new(new(definition.EntryNodeId, "then"), new(forkId, "execute"), "execution"))
            .Add(NewId("link"), new(new(joinId, "then"), entryRoute.Value.Target, "execution"));
        var result = definition with
        {
            SchemaVersion = WorkflowSchemaVersions.ConfigurableParallelGraph,
            Nodes = nodes.SetItem(forkId, nodes[forkId] with { ParallelFork = new(branchDescriptors) }),
            Connections = definition.Connections.Remove(outputData.Key).Add(NewId("link"), new(new(joinId, "out"), new(outputEntry.Key, "in"), "data")),
            ExecutionConnections = execution
        };
        return Checked(result);
    }

    public static WorkflowDefinition RenameConfigurableParallelBranch(WorkflowDefinition definition, string forkNodeId,
        string branchId, string label)
    {
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph) ||
            !ParallelBranchLimits.IsValidId(branchId) || string.IsNullOrWhiteSpace(label) ||
            label.Length > ParallelBranchLimits.MaximumLabelLength ||
            !definition.Nodes.TryGetValue(forkNodeId, out var fork) || fork.Type != NodeTypes.ParallelFork || fork.ParallelFork is not { } configuration)
            throw new ArgumentException("Choose an existing configurable branch and enter a label of 1–64 characters.", nameof(branchId));

        if (!configuration.Branches.Any(branch => branch.Id == branchId))
            throw new ArgumentException("The branch no longer exists.", nameof(branchId));
        var renamed = configuration.Branches
            .Select(branch => branch.Id == branchId ? branch with { Label = label.Trim() } : branch)
            .ToImmutableArray();
        return Checked(definition with
        {
            Nodes = definition.Nodes.SetItem(forkNodeId, fork with { ParallelFork = configuration with { Branches = renamed } })
        });
    }

    /// <summary>Adds an empty, fully connected branch to an existing configurable parallel region.</summary>
    public static WorkflowDefinition AddConfigurableParallelBranch(WorkflowDefinition definition, string forkNodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        var (fork, forkConfiguration, joinId, join, joinConfiguration) = ConfigurableParallelRegion(definition, forkNodeId);
        if (forkConfiguration.Branches.Length >= ParallelGraphLimits.MaximumExplicitBranches)
            throw new ArgumentException($"A parallel region can have at most {ParallelGraphLimits.MaximumExplicitBranches} branches.", nameof(forkNodeId));
        if (definition.Nodes.Count >= 256)
            throw new ArgumentException("The graph has reached its 256-node limit.", nameof(definition));

        var usedIds = forkConfiguration.Branches.Select(branch => branch.Id).ToHashSet(StringComparer.Ordinal);
        var number = 1;
        while (usedIds.Contains($"branch_{number}")) number++;
        var branchId = $"branch_{number}";
        var anchorId = NewId("parallel-branch");
        var branch = new ParallelBranchDescriptor(branchId, $"Branch {number}", anchorId);
        var forkBranches = forkConfiguration.Branches.Add(branch);
        var joinBranches = joinConfiguration.BranchIds.Add(branchId);
        var nodes = definition.Nodes
            .Add(anchorId, new NodeDefinition(NodeTypes.ParallelBranch, "1.0.0")
            {
                ParallelBranch = new(forkNodeId, branchId)
            })
            .SetItem(forkNodeId, fork with { ParallelFork = forkConfiguration with { Branches = forkBranches } })
            .SetItem(joinId, join with { ParallelJoin = joinConfiguration with { BranchIds = joinBranches } });
        var execution = definition.ExecutionConnections!
            .Add(NewId("link"), new(new(forkNodeId, ParallelBranchLimits.PinId(branchId)), new(anchorId, "execute"), "execution"))
            .Add(NewId("link"), new(new(anchorId, "then"), new(joinId, ParallelBranchLimits.PinId(branchId)), "execution"));
        return Checked(definition with { Nodes = nodes, ExecutionConnections = execution });
    }

    /// <summary>Moves one branch in presentation order while preserving its stable identity and all wires.</summary>
    public static WorkflowDefinition MoveConfigurableParallelBranch(WorkflowDefinition definition, string forkNodeId,
        string branchId, int offset)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset), "Move a branch one position up or down.");
        var (fork, forkConfiguration, joinId, join, joinConfiguration) = ConfigurableParallelRegion(definition, forkNodeId);
        var branches = forkConfiguration.Branches;
        var index = Array.FindIndex(branches.ToArray(), branch => branch.Id == branchId);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= branches.Length)
            throw new ArgumentException("Choose a branch that can move in that direction.", nameof(branchId));

        var reordered = branches.SetItem(index, branches[destination]).SetItem(destination, branches[index]);
        var reorderedIds = joinConfiguration.BranchIds.SetItem(index, joinConfiguration.BranchIds[destination])
            .SetItem(destination, joinConfiguration.BranchIds[index]);
        var nodes = definition.Nodes
            .SetItem(forkNodeId, fork with { ParallelFork = forkConfiguration with { Branches = reordered } })
            .SetItem(joinId, join with { ParallelJoin = joinConfiguration with { BranchIds = reorderedIds } });
        return Checked(definition with { Nodes = nodes });
    }

    /// <summary>Removes an empty branch without deleting or disconnecting any branch work.</summary>
    public static WorkflowDefinition RemoveEmptyConfigurableParallelBranch(WorkflowDefinition definition, string forkNodeId,
        string branchId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        var (fork, forkConfiguration, joinId, join, joinConfiguration) = ConfigurableParallelRegion(definition, forkNodeId);
        var branches = forkConfiguration.Branches;
        var index = Array.FindIndex(branches.ToArray(), branch => branch.Id == branchId);
        if (index < 0) throw new ArgumentException("Choose an existing parallel branch.", nameof(branchId));
        if (branches.Length <= ParallelBranchLimits.MinimumBranches)
            throw new ArgumentException($"A parallel region must keep at least {ParallelBranchLimits.MinimumBranches} branches.", nameof(branchId));

        var descriptor = branches[index];
        var execution = definition.ExecutionConnections!;
        var anchorRoute = execution.Values.Single(edge => edge.Source.NodeId == descriptor.AnchorNodeId);
        if (anchorRoute.Target.NodeId != joinId || anchorRoute.Target.PortId != ParallelBranchLimits.PinId(branchId))
            throw new ArgumentException("Remove this branch's Agent nodes before removing the branch. Branch work is preserved.", nameof(branchId));
        _ = execution.Single(edge => edge.Value.Source.NodeId == forkNodeId &&
            edge.Value.Source.PortId == ParallelBranchLimits.PinId(branchId));
        _ = execution.Single(edge => edge.Value.Target.NodeId == joinId &&
            edge.Value.Target.PortId == ParallelBranchLimits.PinId(branchId));
        var remaining = branches.RemoveAt(index);
        var remainingJoinIds = joinConfiguration.BranchIds.RemoveAt(index);
        var nodes = definition.Nodes.Remove(descriptor.AnchorNodeId)
            .SetItem(forkNodeId, fork with { ParallelFork = forkConfiguration with { Branches = remaining } })
            .SetItem(joinId, join with { ParallelJoin = joinConfiguration with { BranchIds = remainingJoinIds } });
        var updatedExecution = execution.RemoveRange(execution.Where(edge =>
            edge.Value.Source.NodeId == forkNodeId && edge.Value.Source.PortId == ParallelBranchLimits.PinId(branchId) ||
            edge.Value.Source.NodeId == descriptor.AnchorNodeId ||
            edge.Value.Target.NodeId == descriptor.AnchorNodeId ||
            edge.Value.Target.NodeId == joinId && edge.Value.Target.PortId == ParallelBranchLimits.PinId(branchId))
            .Select(edge => edge.Key));
        return Checked(definition with { Nodes = nodes, ExecutionConnections = updatedExecution });
    }

    /// <summary>Renames a Sequence output without changing its stable ID or execution wire.</summary>
    public static WorkflowDefinition RenameSequenceOutput(WorkflowDefinition definition, string sequenceNodeId,
        string outputId, string label)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph) ||
            !definition.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is not { } configuration || !SequenceOutputLimits.IsValidId(outputId) ||
            string.IsNullOrWhiteSpace(label) || label.Trim().Length > SequenceOutputLimits.MaximumLabelLength ||
            !configuration.Outputs.Any(output => output.Id == outputId))
            throw new ArgumentException("Choose a Sequence output and enter a label of 1–64 characters.", nameof(outputId));

        var outputs = configuration.Outputs.Select(output => output.Id == outputId ? output with { Label = label.Trim() } : output).ToImmutableArray();
        return Checked(definition with { Nodes = definition.Nodes.SetItem(sequenceNodeId, sequence with { Sequence = configuration with { Outputs = outputs } }) });
    }

    /// <summary>Moves a Sequence output in canonical order while preserving its stable ID and wire.</summary>
    public static WorkflowDefinition MoveSequenceOutput(WorkflowDefinition definition, string sequenceNodeId,
        string outputId, int offset)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph) ||
            !definition.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is not { } configuration)
            throw new ArgumentException("Choose a configured Sequence node.", nameof(sequenceNodeId));

        var index = Array.FindIndex(configuration.Outputs.ToArray(), output => output.Id == outputId);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= configuration.Outputs.Length)
            throw new ArgumentException("Choose an output that can move in that direction.", nameof(outputId));
        var outputs = configuration.Outputs.SetItem(index, configuration.Outputs[destination]).SetItem(destination, configuration.Outputs[index]);
        return Checked(definition with { Nodes = definition.Nodes.SetItem(sequenceNodeId, sequence with { Sequence = configuration with { Outputs = outputs } }) });
    }

    /// <summary>Adds a connected Sequence output targeting the workflow Output node.</summary>
    public static WorkflowDefinition AddSequenceOutput(WorkflowDefinition definition, string sequenceNodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph) ||
            !definition.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is not { } configuration || definition.ExecutionConnections is not { } execution)
            throw new ArgumentException("Choose a configured Sequence node.", nameof(sequenceNodeId));
        if (configuration.Outputs.Length >= SequenceOutputLimits.MaximumOutputs)
            throw new ArgumentException($"A Sequence can have at most {SequenceOutputLimits.MaximumOutputs} outputs.", nameof(sequenceNodeId));
        var outputNode = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (outputNode.Value is null) throw new ArgumentException("The Sequence graph has no Output node.", nameof(sequenceNodeId));
        var usedIds = configuration.Outputs.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var number = 1;
        while (usedIds.Contains($"output_{number}")) number++;
        var outputId = $"output_{number}";
        var descriptor = new SequenceOutputDescriptor(outputId, $"Output {number}");
        var connectionId = NewId("sequence");
        var updated = definition with
        {
            Nodes = definition.Nodes.SetItem(sequenceNodeId, sequence with { Sequence = configuration with { Outputs = configuration.Outputs.Add(descriptor) } }),
            ExecutionConnections = execution.Add(connectionId, new(new(sequenceNodeId, SequenceOutputLimits.PinId(outputId)), new(outputNode.Key, "execute"), "execution"))
        };
        return Checked(updated);
    }

    /// <summary>Removes a Sequence output and its matching execution wire.</summary>
    public static WorkflowDefinition RemoveSequenceOutput(WorkflowDefinition definition, string sequenceNodeId, string outputId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph) ||
            !definition.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is not { } configuration || definition.ExecutionConnections is not { } execution)
            throw new ArgumentException("Choose a configured Sequence node.", nameof(sequenceNodeId));
        if (configuration.Outputs.Length <= SequenceOutputLimits.MinimumOutputs)
            throw new ArgumentException($"A Sequence must keep at least {SequenceOutputLimits.MinimumOutputs} outputs.", nameof(outputId));
        if (!configuration.Outputs.Any(output => output.Id == outputId)) throw new ArgumentException("The Sequence output no longer exists.", nameof(outputId));
        var connection = execution.Single(edge => edge.Value.Source.NodeId == sequenceNodeId && edge.Value.Source.PortId == SequenceOutputLimits.PinId(outputId));
        var updated = definition with
        {
            Nodes = definition.Nodes.SetItem(sequenceNodeId, sequence with { Sequence = configuration with { Outputs = [.. configuration.Outputs.Where(output => output.Id != outputId)] } }),
            ExecutionConnections = execution.Remove(connection.Key)
        };
        return Checked(updated);
    }

    /// <summary>Promotes a valid direct Sequence graph to the bounded downstream-path contract.</summary>
    public static WorkflowDefinition EnableSequencePathGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.SequenceGraph ||
            definition.ExecutionConnections is null || !WorkflowValidator.Validate(definition).IsEmpty)
            throw new ArgumentException("Enable Sequence paths only from a valid schema 1.38 graph.", nameof(definition));
        return definition with { SchemaVersion = WorkflowSchemaVersions.SequencePathGraph };
    }

    /// <summary>Promotes a valid bounded Sequence-path graph to the terminal plain-text Agent path contract.</summary>
    public static WorkflowDefinition EnableSequenceAgentPathGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.SequencePathGraph ||
            definition.ExecutionConnections is null || !WorkflowValidator.Validate(definition).IsEmpty)
            throw new ArgumentException("Enable Sequence Agent paths only from a valid schema 1.43 graph.", nameof(definition));
        return definition with { SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph };
    }

    /// <summary>Promotes a valid bounded Sequence-path graph to the deterministic uppercase-step contract.</summary>
    public static WorkflowDefinition EnableSequenceTransformPathGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.SequencePathGraph ||
            definition.ExecutionConnections is null || !WorkflowValidator.Validate(definition).IsEmpty)
            throw new ArgumentException("Enable Sequence transform paths only from a valid schema 1.43 graph.", nameof(definition));
        return definition with { SchemaVersion = WorkflowSchemaVersions.SequenceTransformPathGraph };
    }

    /// <summary>Promotes a valid deterministic-transform path graph to the reverse-step contract.</summary>
    public static WorkflowDefinition EnableSequenceReversePathGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != WorkflowSchemaVersions.SequenceTransformPathGraph ||
            definition.ExecutionConnections is null || !WorkflowValidator.Validate(definition).IsEmpty)
            throw new ArgumentException("Enable Sequence reverse paths only from a valid schema 1.47 graph.", nameof(definition));
        return definition with { SchemaVersion = WorkflowSchemaVersions.SequenceReversePathGraph };
    }

    /// <summary>Promotes a valid one-transform Sequence path to the bounded two-transform contract.</summary>
    public static WorkflowDefinition EnableSequenceTransformChainGraph(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph) ||
            definition.ExecutionConnections is null || !WorkflowValidator.Validate(definition).IsEmpty)
            throw new ArgumentException("Enable Sequence transform chains only from a valid schema 1.47 or 1.48 graph.", nameof(definition));
        return definition with { SchemaVersion = WorkflowSchemaVersions.SequenceTransformChainGraph };
    }

    private static (NodeDefinition Fork, ParallelForkConfiguration ForkConfiguration, string JoinId, NodeDefinition Join,
        ParallelJoinConfiguration JoinConfiguration) ConfigurableParallelRegion(WorkflowDefinition definition, string forkNodeId)
    {
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph) ||
            !definition.Nodes.TryGetValue(forkNodeId, out var fork) || fork.Type != NodeTypes.ParallelFork ||
            fork.ParallelFork is not { } forkConfiguration || definition.ExecutionConnections is null)
            throw new ArgumentException("Choose a configurable parallel fork in a schema 1.32 graph.", nameof(forkNodeId));
        var joinEntry = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.ParallelJoin);
        if (joinEntry.Value?.ParallelJoin is not { } joinConfiguration || joinConfiguration.ForkNodeId != forkNodeId)
            throw new ArgumentException("The selected fork has no matching join.", nameof(forkNodeId));
        return (fork, forkConfiguration, joinEntry.Key, joinEntry.Value, joinConfiguration);
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
            NodeTypes.BooleanConstant => "boolean", NodeTypes.BooleanNot => "not", NodeTypes.IntegerConstant => "integer", NodeTypes.NumberConstant => "number",
            NodeTypes.IntegerAdd => "add", NodeTypes.NumberAdd => "number-add", NodeTypes.IntegerEquals => "integer-equals", NodeTypes.Sequence => "sequence", NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse => "transform", _ => node.Type
        };
        var id = NewId(prefix);
        return definition with
        {
            SchemaVersion = WorkflowEditing.SchemaFor(definition, node),
            Nodes = definition.Nodes.Add(id, node)
        };
    }

    /// <summary>Duplicates one node's configuration as a new unconnected editable node.</summary>
    /// <remarks>Connections are intentionally not copied; callers own layout/presentation cloning.</remarks>
    public static WorkflowNodeDuplicationResult DuplicateNode(WorkflowDefinition definition, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(nodeId) || !definition.Nodes.TryGetValue(nodeId, out var node))
            throw new ArgumentException("Choose an existing node to duplicate.", nameof(nodeId));

        var duplicated = AddUnconnectedNode(definition, node);
        var newId = duplicated.Nodes.Keys.Except(definition.Nodes.Keys, StringComparer.Ordinal).Single();
        return new(duplicated, newId);
    }

    /// <summary>Reconnects pins in an editable draft. Structural validation remains visible until every issue is fixed.</summary>
    public static WorkflowDefinition ConnectPins(WorkflowDefinition definition, string sourceId, string sourcePortId, string targetId, string targetPortId)
    {
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition))
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

    /// <summary>
    /// Inserts one unary pure data node into an existing explicit data connection.
    /// The connection ID is the authoring selection identity; stale or ambiguous
    /// selections are rejected rather than repairing unrelated topology.
    /// </summary>
    public static WorkflowDefinition InsertPureNodeBetween(WorkflowDefinition definition, string connectionId, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(node);
        if (!WorkflowSchemaVersions.UsesExplicitGraph(definition))
            throw new ArgumentException("Pure pin insertion requires an explicit typed graph.", nameof(definition));
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.Nodes.Count >= 256)
            throw new ArgumentException("The inserted node would exceed the 256-node workflow limit.", nameof(definition));
        if (!definition.Connections.TryGetValue(connectionId, out var selected) || selected.Kind != "data")
            throw new ArgumentException("Choose one existing data connection.", nameof(connectionId));
        if (!definition.Nodes.TryGetValue(selected.Source.NodeId, out var source) ||
            !definition.Nodes.TryGetValue(selected.Target.NodeId, out var target) ||
            selected.Source.NodeId == selected.Target.NodeId)
            throw new ArgumentException("The selected connection must reference two existing nodes.", nameof(connectionId));
        if (!WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) ||
            !string.Equals(node.TypeVersion, contract.TypeVersion, StringComparison.Ordinal) ||
            !contract.IsPure || contract.Effects != WorkflowNodeEffectFlags.None ||
            contract.Activation != WorkflowNodeActivationKind.DataEvaluation || contract.HasDynamicPins)
            throw new ArgumentException("Only current, static, pure data nodes can be inserted.", nameof(node));

        var candidatePorts = WorkflowPorts.Explicit(node);
        var candidateInputs = candidatePorts.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data).ToArray();
        var candidateOutputs = candidatePorts.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data).ToArray();
        if (candidatePorts.Any(port => port.Kind == WorkflowPortKind.Execution) || candidateInputs.Length != 1 || candidateOutputs.Length != 1)
            throw new ArgumentException("The inserted node must have exactly one data input and one data output, with no execution pins.", nameof(node));

        var sourcePort = WorkflowPorts.Explicit(source).SingleOrDefault(port =>
            port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data && port.Id == selected.Source.PortId);
        var targetPort = WorkflowPorts.Explicit(target).SingleOrDefault(port =>
            port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data && port.Id == selected.Target.PortId);
        if (sourcePort is null || targetPort is null || sourcePort.DataType != candidateInputs[0].DataType ||
            candidateOutputs[0].DataType != targetPort.DataType)
            throw new ArgumentException("The pure node's data pins must exactly bridge the selected connection.", nameof(node));
        if (definition.Connections.Values.Any(edge => edge != selected && edge.Target == selected.Target))
            throw new ArgumentException("The selected target input has another data connection.", nameof(connectionId));

        var insertedId = NewId("pure");
        var connections = definition.Connections
            .SetItem(connectionId, new(selected.Source, new(insertedId, candidateInputs[0].Id), "data"))
            .Add(NewId("link"), new(new(insertedId, candidateOutputs[0].Id), selected.Target, "data"));
        return Checked(definition with
        {
            Nodes = definition.Nodes.Add(insertedId, node),
            Connections = connections
        });
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
        if (sourcePort is not null && target.Type == NodeTypes.Output &&
            (definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph))
            target = target with { OutputDataType = sourcePort.Kind == WorkflowPortKind.Data ? sourcePort.DataType : target.OutputDataType };
        var targetPort = WorkflowPorts.Explicit(target).SingleOrDefault(port => port.Direction == WorkflowPortDirection.Input && port.Id == targetPortId);
        if (sourcePort is null || targetPort is null || sourcePort.Kind != targetPort.Kind || sourcePort.DataType != targetPort.DataType)
        {
            var sourceLabel = sourcePort is null ? $"'{sourcePortId}' (undeclared)" : $"'{sourcePort.Id}' ({sourcePort.DataType})";
            var targetLabel = targetPort is null ? $"'{targetPortId}' (undeclared)" : $"'{targetPort.Id}' ({targetPort.DataType})";
            throw new ArgumentException($"Cannot connect {sourceLabel} to {targetLabel}: pins must have the same kind and exact data type. Add an explicit conversion node when a conversion is needed.");
        }

        var connectionId = NewId("link");
        var connection = new ConnectionDefinition(new(sourceId, sourcePortId), new(targetId, targetPortId),
            sourcePort.Kind == WorkflowPortKind.Execution ? "execution" : "data");
        if (sourcePort.Kind == WorkflowPortKind.Execution)
        {
            var edges = definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal);
            var allowsAlternativeInput = definition.SchemaVersion is WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph &&
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
        var nodes = target.Type == NodeTypes.Output && sourcePort.Kind == WorkflowPortKind.Data
            ? definition.Nodes.SetItem(targetId, target with { OutputDataType = sourcePort.DataType })
            : definition.Nodes;
        return definition with { Nodes = nodes, Connections = data.Add(connectionId, connection) };
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
            SchemaVersion = WorkflowEditing.SchemaFor(definition, node),
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
        if (!WorkflowDraftEditing.HasOnlyDraftableIssues(definition)) WorkflowValidator.ThrowIfInvalid(definition);
        if (!definition.Nodes.TryGetValue(id, out var existing)) throw new ArgumentException("Unknown node.");
        if (existing.Type == node.Type && existing.Type is NodeTypes.Condition or NodeTypes.Approval or NodeTypes.Loop or NodeTypes.StringConstant or NodeTypes.BooleanConstant or NodeTypes.IntegerConstant or NodeTypes.NumberConstant)
            return WorkflowDraftEditing.ValidateEdit(definition with { Nodes = definition.Nodes.SetItem(id, node) });
        RequireProcessing(existing.Type);
        RequireProcessing(node.Type);
        return WorkflowDraftEditing.ValidateEdit(
            definition with { SchemaVersion = WorkflowEditing.SchemaFor(definition, node), Nodes = definition.Nodes.SetItem(id, node) });
    }

    public static WorkflowDefinition RemoveNode(WorkflowDefinition definition, string id)
    {
        if (!definition.Nodes.TryGetValue(id, out var requestedNode)) throw new ArgumentException("Unknown node.", nameof(id));
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition))
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
        if (type is not (NodeTypes.Uppercase or NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse or NodeTypes.Reverse or NodeTypes.McpTool or NodeTypes.Agent or NodeTypes.WorkflowValue or NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.TextTemplate or NodeTypes.WorkflowCall))
            throw new ArgumentException("Only processing nodes can be inserted, replaced or removed here.");
    }
}
