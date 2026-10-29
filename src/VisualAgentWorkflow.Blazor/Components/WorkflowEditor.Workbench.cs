using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowEditor
{
    private static WorkflowPortDescriptor LegacyDataPort(WorkflowPort port, WorkflowPortDirection direction) =>
        new(port.Id, direction, WorkflowPortKind.Data, port.DataType)
        {
            Required = direction == WorkflowPortDirection.Input,
            Cardinality = direction == WorkflowPortDirection.Input
                ? WorkflowPortCardinality.ExactlyOne
                : WorkflowPortCardinality.Many
        };

    private static WorkflowNodeLibraryEntry ContractEntry(string type, bool enabled, string? description = null)
    {
        var contract = WorkflowNodeContractCatalog.Get(type);
        return new(type, contract.DisplayName, contract.Category, description ?? contract.Description, enabled)
        {
            Effects = contract.Effects,
            Authoring = contract.Authoring
        };
    }

    private string? insertionConnection;
    private bool UseGraphRemoval => WorkflowSchemaVersions.UsesExplicitGraph(Definition) || UsesGraphEditing || WorkflowValidator.Validate(Definition).Any(issue =>
        issue.Code is "graph.degree" or "workflow.degree" or "graph.unreachable" or "graph.cycle" or "workflow.terminals");
    private ImmutableArray<WorkflowPort> SelectedOutputs => selectedNode is { } id && Definition.Nodes.TryGetValue(id, out var node)
        ? [.. CanvasPorts(node).Where(port => port.Direction == WorkflowPortDirection.Output)
            .Select(port => new WorkflowPort(port.Id, port.DataType ?? "execution"))]
        : [];
    private Task ConnectionSelected(string id)
    {
        var connectionId = id.StartsWith("execution:", StringComparison.Ordinal) ? id["execution:".Length..] :
            id.StartsWith("data:", StringComparison.Ordinal) ? id["data:".Length..] : id;
        var edge = id.StartsWith("execution:", StringComparison.Ordinal)
            ? Definition.ExecutionConnections?.GetValueOrDefault(connectionId)
            : Definition.Connections.GetValueOrDefault(connectionId);
        if (edge is not null)
        {
            insertionConnection = WorkflowSchemaVersions.UsesExplicitGraph(Definition) ? null : id;
            selectedNode = edge.Source.NodeId;
            selectedPort = edge.Source.PortId;
        }
        return Task.CompletedTask;
    }

    private PortReference? Insertion(GraphContextRequest? context)
    {
        // Only a command opened directly on a wire is an explicit insert operation.
        // A selected node or previously selected wire must never make a library click auto-connect.
        var connection = WorkflowSchemaVersions.UsesExplicitGraph(Definition) ? null : context?.ConnectionId;
        if (connection is not null && Definition.Connections.TryGetValue(connection, out var edge)) return edge.Source;
        return null;
    }

    private string InsertionDescription(GraphContextRequest? context)
    {
        if (context is { ConnectionId: not null, SourceNodeId: { } wireSourceNodeId, SourcePortId: { } wireSourcePortId,
            TargetNodeId: { } wireTargetNodeId, TargetPortId: { } wireTargetPortId })
            return $"Insert a compatible pure node between {NodeLabel(wireSourceNodeId)} · {wireSourcePortId} and {NodeLabel(wireTargetNodeId)} · {wireTargetPortId}.";

        if (context is { SourceNodeId: { } sourceNodeId, SourcePortId: { } sourcePortId } &&
            Definition.Nodes.TryGetValue(sourceNodeId, out var sourceNode))
        {
            var sourcePort = CanvasPorts(sourceNode).FirstOrDefault(port => port.Direction == WorkflowPortDirection.Output && port.Id == sourcePortId);
            var sourceType = sourcePort?.DataType ?? (sourcePort?.Kind == WorkflowPortKind.Execution ? "execution" : "unknown");
            return $"Create from {NodeLabel(sourceNodeId)} · {sourcePortId}. Showing nodes with a compatible {sourceType} input.";
        }

        return Insertion(context) is { } source
            ? $"Insert after {(string.IsNullOrWhiteSpace(EffectiveLayout.Nodes[source.NodeId].Label) ? WorkflowGraphCanvas.FriendlyName(Definition.Nodes[source.NodeId]) : EffectiveLayout.Nodes[source.NodeId].Label)} · {source.PortId}"
            : "Select an insertion path.";
    }

    private string NodeLabel(string id) => string.IsNullOrWhiteSpace(EffectiveLayout.Nodes.GetValueOrDefault(id)?.Label)
        ? WorkflowGraphCanvas.FriendlyName(Definition.Nodes[id])
        : EffectiveLayout.Nodes[id].Label;

    private string ContextualLibraryEmptyMessage(GraphContextRequest context) =>
        context.SourceNodeId is not null
            ? "No node in this graph accepts that output directly. Add an explicit conversion node, or drag to a compatible input pin."
            : "No matching actions. Try another name.";

    private bool CanInsertStringIntegerConversion(GraphContextRequest context)
    {
        if (context.SourceNodeId is null || context.SourcePortId is null ||
            context.TargetNodeId is null || context.TargetPortId is null ||
            Definition.SchemaVersion != WorkflowSchemaVersions.StringIntegerConversionGraph)
            return false;
        try
        {
            GraphEditing.InsertStringIntegerConversion(Definition, context.SourceNodeId, context.SourcePortId,
                context.TargetNodeId, context.TargetPortId);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private async Task InsertStringIntegerConversion(GraphContextRequest context)
    {
        if (!CanInsertStringIntegerConversion(context)) return;
        await CloseMenu(context);
        await Edit(() => GraphEditing.InsertStringIntegerConversion(Definition, context.SourceNodeId!, context.SourcePortId!,
            context.TargetNodeId!, context.TargetPortId!), context);
    }

    private IReadOnlyList<WorkflowNodeLibraryEntry> FilterBySourcePin(IReadOnlyList<WorkflowNodeLibraryEntry> entries, GraphContextRequest? context)
    {
        if (!WorkflowSchemaVersions.UsesExplicitGraph(Definition) || context?.SourceNodeId is not { } sourceNodeId ||
            context.SourcePortId is not { } sourcePortId || !Definition.Nodes.TryGetValue(sourceNodeId, out var sourceNode))
            return entries;

        if (context.ConnectionId is { } connectionId && context.TargetNodeId is { } targetNodeId &&
            context.TargetPortId is { } targetPortId && Definition.Nodes.ContainsKey(targetNodeId))
        {
            return entries.Where(entry =>
            {
                if (entry.Key.StartsWith("template:", StringComparison.Ordinal)) return false;
                try
                {
                    // The Core operation is the authoritative candidate filter:
                    // the picker must never advertise a node the atomic edit will reject.
                    _ = GraphEditing.InsertPureNodeBetween(Definition, connectionId, FromSelection(entry.Key));
                    return true;
                }
                catch (ArgumentException) { return false; }
            }).ToArray();
        }

        var sourcePort = CanvasPorts(sourceNode).FirstOrDefault(port => port.Direction == WorkflowPortDirection.Output && port.Id == sourcePortId);
        if (sourcePort is null) return entries;

        return entries.Where(entry =>
        {
            if (entry.Key.StartsWith("template:", StringComparison.Ordinal)) return true;
            NodeDefinition candidate;
            try { candidate = FromSelection(entry.Key); }
            catch (ArgumentException) { return false; }
            return CanvasPorts(candidate).Any(port => port.Direction == WorkflowPortDirection.Input &&
                port.Kind == sourcePort.Kind && port.DataType == sourcePort.DataType);
        }).ToArray();
    }

    private bool CanUpgradeBlankGraph => !ReadOnly && !Disabled && Definition.SchemaVersion != "1.23" &&
        Definition.Nodes.Count == 2 && Definition.Nodes.TryGetValue(Definition.EntryNodeId, out var entry) && entry.Type == NodeTypes.Input &&
        Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Output) == 1 && Definition.Connections.Count == 1 &&
        Definition.Connections.Values.Any(edge => edge.Source.NodeId == Definition.EntryNodeId && edge.Source.PortId == "out" &&
            Definition.Nodes.TryGetValue(edge.Target.NodeId, out var target) && target.Type == NodeTypes.Output && edge.Target.PortId == "in");

    private bool CanUpgradeMutableVariablesGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph &&
        Definition.Nodes.Count == 2 && Definition.Nodes.TryGetValue(Definition.EntryNodeId, out var variableEntry) && variableEntry.Type == NodeTypes.Input &&
        Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Output) == 1 && Definition.Connections.Count == 1 &&
        Definition.ExecutionConnections is { Count: 1 } execution && Definition.Connections.Values.Any(edge => edge.Source.NodeId == Definition.EntryNodeId && edge.Source.PortId == "out" &&
            Definition.Nodes.TryGetValue(edge.Target.NodeId, out var target) && target.Type == NodeTypes.Output && edge.Target.PortId == "in") &&
        execution.Values.Any(edge => edge.Source.NodeId == Definition.EntryNodeId && edge.Target.NodeId == Definition.Nodes.Single(node => node.Value.Type == NodeTypes.Output).Key);

    private Task EnableMutableVariablesPreview() => Edit(() => GraphEditing.EnableMutableVariablesGraph(Definition));

    private Task EnableExplicitPreview() => Edit(() => GraphEditing.EnableExplicitGraph(Definition));
    private bool CanUpgradeDirectMcpGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableDirectMcpPreview() => Edit(() => GraphEditing.EnableDirectMcpGraph(Definition));
    private bool CanUpgradeBranchGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableBranchPreview() => Edit(() => GraphEditing.EnableExplicitBranchGraph(Definition));
    private bool CanUpgradeTypedBooleanGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.ExplicitBranchGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableTypedBooleanPreview() => Edit(() => GraphEditing.EnableTypedBooleanGraph(Definition));
    private bool CanUpgradeBooleanTopology => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.TypedBooleanGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableBooleanTopologyPreview() => Edit(() => GraphEditing.EnableBooleanTopologyGraph(Definition));
    private bool CanUpgradePureGraphFunctions => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.BooleanTopologyGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnablePureGraphFunctionsPreview() => Edit(() => GraphEditing.EnablePureGraphFunctions(Definition));
    private bool CanUpgradeAgentGraphFunctionTools => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.PureGraphFunctions &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableAgentGraphFunctionToolsPreview() => Edit(() => GraphEditing.EnableAgentGraphFunctionTools(Definition));
    private bool CanUpgradeIntegerGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.AgentExecutionRoutes &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableIntegerGraphPreview() => Edit(() => GraphEditing.EnableIntegerGraph(Definition));
    private bool CanUpgradeStringIntegerConversionGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.IntegerGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableStringIntegerConversionPreview() => Edit(() => GraphEditing.EnableStringIntegerConversionGraph(Definition));
    private bool CanUpgradeNumberGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.DirectMcpPinsGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableNumberGraphPreview() => Edit(() => GraphEditing.EnableNumberGraph(Definition));
    private bool CanUpgradeNumberPropagationGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.NumberGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableNumberPropagationPreview() => Edit(() => GraphEditing.EnableNumberPropagationGraph(Definition));
    private bool CanUpgradeSequencePathGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.SequenceGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableSequencePathPreview() => Edit(() => GraphEditing.EnableSequencePathGraph(Definition));
    private bool CanUpgradeSequenceAgentPathGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.SequencePathGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableSequenceAgentPathPreview() => Edit(() => GraphEditing.EnableSequenceAgentPathGraph(Definition));
    private bool CanUpgradeSequenceTransformPathGraph => !ReadOnly && !Disabled && Definition.SchemaVersion == WorkflowSchemaVersions.SequencePathGraph &&
        WorkflowValidator.Validate(Definition).IsEmpty;
    private Task EnableSequenceTransformPathPreview() => Edit(() => GraphEditing.EnableSequenceTransformPathGraph(Definition));
    private int configurableParallelBranchCount = 3;
    private bool CanCreateConfigurableParallelGraph
    {
        get
        {
            if (ReadOnly || Disabled || Definition.SchemaVersion != WorkflowSchemaVersions.IntegerGraph ||
                Definition.Nodes.Count > 256 - configurableParallelBranchCount - 2 ||
                !Definition.Nodes.TryGetValue(Definition.EntryNodeId, out var input) || input.Type != NodeTypes.Input ||
                Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Output) != 1 ||
                Definition.ExecutionConnections is not { } execution ||
                Definition.Nodes.Values.Any(node => node.Type is NodeTypes.ParallelFork or NodeTypes.Loop) ||
                !WorkflowValidator.Validate(Definition).IsEmpty)
                return false;

            var output = Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
            var outputConnection = Definition.Connections.Values.SingleOrDefault(edge =>
                edge.Target.NodeId == output && edge.Target.PortId == "in");
            return execution.Values.Count(edge => edge.Source.NodeId == Definition.EntryNodeId) == 1 &&
                outputConnection is not null &&
                outputConnection.Source == new PortReference(Definition.EntryNodeId, "out");
        }
    }
    private Task CreateConfigurableParallelGraph() => Edit(() =>
        GraphEditing.CreateConfigurableParallelGraph(Definition, configurableParallelBranchCount));
    private Task AddConfigurableParallelBranch(string forkNodeId) => Edit(() =>
        GraphEditing.AddConfigurableParallelBranch(Definition, forkNodeId), preserveSelection: forkNodeId);
    private Task MoveConfigurableParallelBranch(string forkNodeId, string branchId, int offset) => Edit(() =>
        GraphEditing.MoveConfigurableParallelBranch(Definition, forkNodeId, branchId, offset));
    private Task RemoveEmptyConfigurableParallelBranch(string forkNodeId, string branchId) => Edit(() =>
        GraphEditing.RemoveEmptyConfigurableParallelBranch(Definition, forkNodeId, branchId));
    private bool CanRemoveEmptyConfigurableParallelBranch(string forkNodeId, ParallelBranchDescriptor branch)
    {
            if (Definition.SchemaVersion is not (WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph) ||
            !Definition.Nodes.TryGetValue(forkNodeId, out var fork) || fork.ParallelFork is not { } configuration ||
            configuration.Branches.Length <= ParallelBranchLimits.MinimumBranches || Definition.ExecutionConnections is not { } execution)
            return false;
        var join = Definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.ParallelJoin);
        if (join.Value is null) return false;
        var routes = execution.Values.Where(edge => edge.Source.NodeId == branch.AnchorNodeId).ToArray();
        return routes.Length == 1 && routes[0].Target.NodeId == join.Key &&
            routes[0].Target.PortId == ParallelBranchLimits.PinId(branch.Id);
    }

    private IReadOnlyList<WorkflowNodeLibraryEntry> LibraryEntries(GraphContextRequest? context = null)
    {
        if (Definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph)
            return
            [
                ContractEntry(NodeTypes.StringConstant, !ReadOnly && !Disabled && Definition.Nodes.Count < 256),
                ContractEntry(NodeTypes.TextConcat, !ReadOnly && !Disabled && Definition.Nodes.Count < 256)
            ];
        if (WorkflowSchemaVersions.UsesExplicitGraph(Definition) && Definition.SchemaVersion is WorkflowSchemaVersions.ExplicitBranchGraph or WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.DirectMcpGraph or WorkflowSchemaVersions.DirectMcpPinsGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.StringIntegerConversionGraph or WorkflowSchemaVersions.NumberGraph or WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph or WorkflowSchemaVersions.MutableVariablesGraph)
        {
            var explicitEntries = new List<WorkflowNodeLibraryEntry>
            {
                ContractEntry(NodeTypes.StringConstant, !ReadOnly && !Disabled && Definition.Nodes.Count < 256),
                ContractEntry(NodeTypes.TextConcat, !ReadOnly && !Disabled && Definition.Nodes.Count < 256),
                ContractEntry(NodeTypes.StringEquals, !ReadOnly && !Disabled && Definition.Nodes.Count < 256),
                ContractEntry(NodeTypes.BooleanBranch, !ReadOnly && !Disabled && Definition.Nodes.Count < 256 &&
                    (Definition.SchemaVersion is WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph
                        ? Definition.Nodes.Values.Count(node => node.Type == NodeTypes.BooleanBranch) < 8
                        : Definition.Nodes.Values.All(node => node.Type != NodeTypes.BooleanBranch))),
                ContractEntry(NodeTypes.Terminate, !ReadOnly && !Disabled && Definition.Nodes.Count < 256 &&
                    Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Terminate) < (Definition.SchemaVersion is WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph ? 16 : 1))
            };
            if (Definition.SchemaVersion is WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                explicitEntries.Insert(0, ContractEntry(NodeTypes.BooleanConstant, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
                explicitEntries.Insert(1, ContractEntry(NodeTypes.BooleanNot, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
            }
            if (Definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.StringIntegerConversionGraph)
            {
                explicitEntries.Insert(0, ContractEntry(NodeTypes.IntegerConstant, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
                explicitEntries.Insert(1, ContractEntry(NodeTypes.IntegerAdd, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
                explicitEntries.Insert(2, ContractEntry(NodeTypes.IntegerEquals, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
                if (ParameterDefinitions.Values.Any(parameter => WorkflowParameterGraphTypes.DataType(parameter, Definition.SchemaVersion is WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph) is not null))
                    explicitEntries.Insert(3, ContractEntry(NodeTypes.WorkflowValue, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
            }
            if (Definition.SchemaVersion == WorkflowSchemaVersions.StringIntegerConversionGraph)
                explicitEntries.Insert(3, ContractEntry(NodeTypes.StringToInteger, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
            if (Definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.NumberGraph or WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph)
                explicitEntries.Insert(0, ContractEntry(NodeTypes.NumberConstant, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
            if (Definition.SchemaVersion == WorkflowSchemaVersions.NumberArithmeticGraph)
                explicitEntries.Insert(1, ContractEntry(NodeTypes.NumberAdd, !ReadOnly && !Disabled && Definition.Nodes.Count < 256));
            if (Definition.SchemaVersion == WorkflowSchemaVersions.MutableVariablesGraph && Definition.Variables is { Count: > 0 })
            {
                explicitEntries.Add(ContractEntry(NodeTypes.VariableGet, !ReadOnly && !Disabled && Definition.Nodes.Count < 256,
                    "Read a declared run-scoped scalar variable."));
                explicitEntries.Add(ContractEntry(NodeTypes.VariableSet, !ReadOnly && !Disabled && Definition.Nodes.Count < 256,
                    "Write a declared run-scoped scalar variable."));
            }
            if (Definition.SchemaVersion is WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
                explicitEntries.AddRange((FunctionCatalog?.List() ?? []).Select(entry => new WorkflowNodeLibraryEntry(
                    FunctionKey(entry.Function), $"Function · {entry.Function.Definition.DisplayName} v{entry.Function.Definition.Version}",
                    "Functions", $"{entry.Function.Definition.Inputs.Count} inputs · {entry.Function.Definition.Outputs.Count} outputs · snapshot pinned on insert",
                        !ReadOnly && !Disabled && Definition.Nodes.Count < 256)));
            if (Definition.SchemaVersion is WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.SequenceAgentPathGraph)
                explicitEntries.AddRange(Providers.Select(provider => new WorkflowNodeLibraryEntry(
                    ProviderKey(provider), provider.DisplayName, "Agents", $"Agent node · {provider.ModelId}",
                    !ReadOnly && !Disabled && Definition.Nodes.Count < 256)));
            if (Definition.SchemaVersion is WorkflowSchemaVersions.DirectMcpGraph or WorkflowSchemaVersions.DirectMcpPinsGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.NumberGraph)
                explicitEntries.InsertRange(0, Bindings.Select(binding => new WorkflowNodeLibraryEntry(
                    ToolKey(binding), binding.DisplayName, "MCP tools", "Direct deterministic call; node grants and the pinned tool contract still apply",
                    !ReadOnly && !Disabled && Definition.Nodes.Count < 256)));
            if (Definition.SchemaVersion is WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph)
                explicitEntries.Insert(0, ContractEntry(NodeTypes.Sequence, !ReadOnly && !Disabled && Definition.Nodes.Count < 256,
                    "Fan out ordered execution pins; each output must be connected exactly once."));
            if (Definition.SchemaVersion == WorkflowSchemaVersions.SequenceTransformPathGraph)
                explicitEntries.Insert(0, ContractEntry(NodeTypes.SequenceUppercase, !ReadOnly && !Disabled && Definition.Nodes.Count < 256,
                    "Uppercase the incoming path text as the terminal deterministic Sequence step."));
            if (Definition.SchemaVersion is WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph)
                explicitEntries.Add(ContractEntry(NodeTypes.ParallelRendezvous, !ReadOnly && !Disabled && Definition.Nodes.Count < 256 && Definition.Nodes.Values.All(node => node.Type != NodeTypes.ParallelRendezvous), "Add one unwired communication barrier; configure its branch pins and grants in node properties"));
            explicitEntries.AddRange(TemplateEntries());
            return FilterBySourcePin(explicitEntries, context);
        }
        var insertion = Insertion(context);
        var sourceType = insertion is { } source
            ? WorkflowPorts.Outputs(Definition.Nodes[source.NodeId]).Single(port => port.Id == source.PortId).DataType
            : null;
        var canCreate = !ReadOnly && !Disabled && Definition.Nodes.Count < 256;
        var canInsert = canCreate && sourceType is not null;
        var textPath = canInsert && sourceType == "string";
        var jsonPath = canInsert && sourceType == "json";
        var jsonSelectorWouldBreakPath = insertion is { } jsonSource &&
            Definition.Nodes[jsonSource.NodeId].Type == NodeTypes.ParallelJoin &&
            Definition.Connections.Values.FirstOrDefault(edge => edge.Source == jsonSource) is { } joinEdge &&
            Definition.Nodes[joinEdge.Target.NodeId].Type == NodeTypes.JsonSelectString;
        var selected = context?.NodeId ?? selectedNode;
        var hasLoop = Definition.Nodes.Values.Any(node => node.Type == NodeTypes.Loop);
        var canCreateCondition = canCreate && !hasLoop && !Definition.Nodes.Values.Any(node => node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin);
        var canInsertLoop = textPath && Definition.Nodes.Count <= 254 && !WorkflowGraph.HasControlNodes(Definition) && !WorkflowGraph.HasTypedJson(Definition);
        List<WorkflowNodeLibraryEntry> entries =
        [
            ContractEntry(NodeTypes.Uppercase, canCreate),
            ContractEntry(NodeTypes.Reverse, canCreate),
            ContractEntry(NodeTypes.TextTemplate, canCreate),
            ContractEntry(NodeTypes.WorkflowValue, canCreate && ParameterDefinitions.Count > 0),
            ContractEntry(NodeTypes.JsonProject, jsonPath),
            new("json-selector", "Select JSON value as text", "Data", "Convert a selected JSON value to text", jsonPath && !jsonSelectorWouldBreakPath),
            new("parallel", "Parallel block", "Flow", "Run two visible branch paths concurrently and collect named results", textPath && Definition.Nodes.Count <= 251 && !hasLoop && !Definition.Nodes.Values.Any(node => node.Type == NodeTypes.ParallelFork)),
            ContractEntry(NodeTypes.Condition, canCreateCondition, "Add an unconnected true / false branch node"),
            ContractEntry(NodeTypes.Merge, canCreate, "Add an unconnected join for routes that should share downstream work"),
            new("loop", "Bounded loop", "Flow", "Repeat one linear body while a text condition holds, up to a fixed iteration limit", canInsertLoop),
            ContractEntry(NodeTypes.Terminate, canCreate && Definition.Nodes.Values.Count(node => node.Type == NodeTypes.Terminate) < 16, "Add an unconnected success or failure exit"),
            ContractEntry(NodeTypes.Approval, !hasLoop && textPath && selected is not null && Definition.Nodes.TryGetValue(selected, out var selectedDefinition) && selectedDefinition.Type == NodeTypes.McpTool && !GuardedTool(selected), "Select an MCP tool to require exact-action review")
        ];
        if (Definition.SchemaVersion == WorkflowSchemaVersions.SequenceTransformPathGraph)
            entries.Insert(0, ContractEntry(NodeTypes.SequenceUppercase, canCreate, "Uppercase text as the terminal deterministic Sequence path step"));
        entries.InsertRange(0, Bindings.Select(b => new WorkflowNodeLibraryEntry(
            ToolKey(b), b.DisplayName, "MCP tools", "Host-registered tool; node grants still apply", canCreate)));
        entries.AddRange(Providers.Select(p => new WorkflowNodeLibraryEntry(ProviderKey(p), p.DisplayName, "Agents", p.ModelId, canCreate)));
        entries.AddRange(TemplateEntries());
        return entries;
    }

    private IReadOnlyList<WorkflowNodeLibraryEntry> TemplateEntries() =>
        TemplateCatalog?.List().Select(entry => new WorkflowNodeLibraryEntry(
            TemplateKey(entry.Template.Definition),
            $"Template · {entry.Template.Definition.DisplayName} v{entry.Template.Definition.Version}",
            "Templates",
            $"Pure graph copy · {entry.Template.Definition.Body.Inputs.Count} inputs · {entry.Template.Definition.Body.Outputs.Count} outputs",
            !ReadOnly && !Disabled && Definition.Nodes.Count < 256)).ToArray() ?? [];

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
        await CloseMenu(context);
        if (context?.ConnectionId is { } connectionId && context.TargetNodeId is not null &&
            WorkflowSchemaVersions.UsesExplicitGraph(Definition))
        {
            try
            {
                var node = FromSelection(key);
                await Edit(() => GraphEditing.InsertPureNodeBetween(Definition, connectionId, node), context);
            }
            catch (ArgumentException exception) { error = $"The pure node could not be inserted: {exception.Message}"; }
            insertionConnection = null;
            return;
        }
        if (key.StartsWith("template:", StringComparison.Ordinal))
        {
            OpenTemplateDialog(key, context);
            return;
        }
        if (key == NodeTypes.Approval)
        {
            var target = context?.NodeId ?? selectedNode!;
            await Edit(() => GraphEditing.InsertApprovalBefore(Definition, target), context, insertion);
        }
        else if (key == NodeTypes.Condition)
            await Edit(() => GraphEditing.AddUnconnectedNode(Definition, new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") }), context, insertion);
        else if (key == NodeTypes.Merge)
            await Edit(() => GraphEditing.AddUnconnectedNode(Definition, new(NodeTypes.Merge, "1.0.0")), context, insertion);
        else if (key == "parallel" && insertion is { } parallelInsertion)
            await Edit(() => GraphEditing.InsertParallelBlockAfter(Definition, parallelInsertion.NodeId, parallelInsertion.PortId), context, parallelInsertion);
        else if (key == "loop" && insertion is { } loopInsertion)
            await Edit(() => GraphEditing.InsertBoundedLoopAfter(Definition, loopInsertion.NodeId, loopInsertion.PortId), context, loopInsertion);
        else if (key == NodeTypes.Terminate)
            await Edit(() => GraphEditing.AddUnconnectedNode(Definition, new(NodeTypes.Terminate, "1.0.0")), context, insertion);
        else if (key == NodeTypes.ParallelRendezvous)
            await Edit(() => GraphEditing.AddUnconnectedNode(Definition with { SchemaVersion = WorkflowSchemaVersions.CollaborativeParallelGraph }, new(NodeTypes.ParallelRendezvous, "1.0.0")), context);
        else if (key == "json-selector" && insertion is { } jsonInsertion)
            await Edit(() => GraphEditing.InsertJsonSelectorAfterJson(Definition, jsonInsertion.NodeId, jsonInsertion.PortId), context, jsonInsertion);
        else if (insertion is not null)
            await Edit(() =>
            {
                var node = FromSelection(key);
                var inserted = UsesGraphEditing ? GraphEditing.InsertNodeAfter(Definition, insertion.NodeId, insertion.PortId, node) :
                    WorkflowEditing.InsertNodeAfter(Definition, insertion.NodeId, node);
                return UpgradeForBranchLocalRouting(inserted, node);
            }, context, insertion);
        else
            await Edit(() =>
            {
                var node = FromSelection(key);
                var added = GraphEditing.AddUnconnectedNode(Definition, node);
                return UpgradeForBranchLocalRouting(added, node);
            }, context);
        insertionConnection = null;
    }

    private static WorkflowDefinition UpgradeForBranchLocalRouting(WorkflowDefinition definition, NodeDefinition node) =>
        // Apply the upgrade after GraphEditing has computed its node schema so that this opt-in
        // authoring action is not overwritten while the unconnected node is added.
        node.Type == NodeTypes.BooleanBranch && definition.SchemaVersion == WorkflowSchemaVersions.ConfigurableParallelGraph
            ? definition with { SchemaVersion = WorkflowSchemaVersions.BranchLocalParallelGraph }
            : definition;

    private Task SetTerminationOutcome(string id, ChangeEventArgs args) => Edit(() =>
        WorkflowTerminationEditing.SetOutcome(Definition, id, args.Value?.ToString() ?? string.Empty));

    private Task AddConditionNode() => Edit(() => GraphEditing.AddUnconnectedNode(Definition,
        new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") }));

    private bool CanRemove(string id) => !ReadOnly && !Disabled && Definition.Nodes.TryGetValue(id, out var node) &&
        node.Type is not (NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.LoopBody) && !GuardedTool(id);
    private bool CanDuplicate(string id) => CanRemove(id) && Definition.Nodes[id].Type is not
        (NodeTypes.Loop or NodeTypes.Approval or NodeTypes.Merge or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous);
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
            Definition.Nodes[id].Type == NodeTypes.ParallelRendezvous ? RemoveRendezvous(Definition, id) :
            UseGraphRemoval ? GraphEditing.RemoveNode(Definition, id) : WorkflowEditing.Remove(Definition, id));
    }

    private static WorkflowDefinition RemoveRendezvous(WorkflowDefinition definition, string id)
    {
        var region = definition.ParallelCollaborationRegions?.GetValueOrDefault(id) ??
            definition.ParallelCollaborationRegions?.Values.FirstOrDefault(item => item.SecondRendezvousNodeId == id);
        var removeIds = definition.SchemaVersion == WorkflowSchemaVersions.PhasedCollaborativeParallelGraph
            ? definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelRendezvous).Select(entry => entry.Key).Distinct(StringComparer.Ordinal).ToArray()
            : region?.SecondRendezvousNodeId is { } secondId
                ? new[] { region.RendezvousNodeId, secondId }
                : [id];
        var next = removeIds.Where(definition.Nodes.ContainsKey)
            .Aggregate(definition, (current, nodeId) => GraphEditing.RemoveNode(current, nodeId));
        return next with
        {
            SchemaVersion = WorkflowSchemaVersions.ConfigurableParallelGraph,
            ParallelCollaborationRegions = null
        };
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
            // A newly added condition is intentionally allowed to be an invalid,
            // unconnected draft. Block removal requires a valid topology, so remove
            // this incomplete node directly and let the Problems pane report any
            // remaining issues.
            if (!WorkflowValidator.Validate(Definition).IsEmpty)
            {
                await Edit(() => GraphEditing.RemoveNode(Definition, id));
                return;
            }
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
        await Edit(() => GraphEditing.DuplicateNode(Definition, id).Definition, context, copyPresentationFrom: id);
    }

    private bool CanCopyClipboardNode(string id)
    {
        if (ReadOnly || Disabled || !WorkflowSchemaVersions.UsesExplicitGraph(Definition) ||
            !Definition.Nodes.TryGetValue(id, out var node) ||
            !WorkflowNodeContractCatalog.TryGet(node.Type, out var contract)) return false;
        return contract.IsPure && contract.Effects == WorkflowNodeEffectFlags.None &&
            contract.State == WorkflowNodeStateKind.Stateless && !contract.HasDynamicPins &&
            !contract.HasExecutionPins && node.Origin is null;
    }

    private async Task CopyClipboardSelection(string[] ids, GraphContextRequest? context)
    {
        var id = ids.FirstOrDefault() ?? context?.NodeId;
        if (id is null) return;
        if (!CanCopyClipboardNode(id)) return;
        try
        {
            var selected = ids.Length > 0 ? ids : canvas is null ? [] : await canvas.SelectedNodeIds();
            var nodeIds = selected.Length > 0 && selected.Contains(id, StringComparer.Ordinal) ? selected : [id];
            if (nodeIds.Any(nodeId => !CanCopyClipboardNode(nodeId)))
                throw new ArgumentException("Every selected node must be a stateless pure data node without execution pins.");
            var selectedSet = nodeIds.ToHashSet(StringComparer.Ordinal);
            var incoming = Definition.Connections.Values
                .Where(edge => !selectedSet.Contains(edge.Source.NodeId) && selectedSet.Contains(edge.Target.NodeId))
                .Select(edge => new GraphClipboardInputBoundary(
                    ClipboardBoundaryId("input", edge.Target.NodeId, edge.Target.PortId),
                    DataPortType(edge.Source.NodeId, edge.Source.PortId, WorkflowPortDirection.Output),
                    edge.Target.NodeId, edge.Target.PortId))
                .ToArray();
            var outgoing = Definition.Connections.Values
                .Where(edge => selectedSet.Contains(edge.Source.NodeId) && !selectedSet.Contains(edge.Target.NodeId))
                .Select(edge => new GraphClipboardOutputBoundary(
                    ClipboardBoundaryId("output", edge.Source.NodeId, edge.Source.PortId),
                    DataPortType(edge.Target.NodeId, edge.Target.PortId, WorkflowPortDirection.Input),
                    edge.Source.NodeId, edge.Source.PortId))
                .ToArray();
            var fragment = GraphClipboardEditing.Extract(Definition, EffectiveLayout, nodeIds, incoming, outgoing);
            await JavaScript.InvokeVoidAsync("navigator.clipboard.writeText", GraphClipboardJson.Serialize(fragment));
            error = null;
            await CloseMenu(context);
        }
        catch (JSException)
        {
            error = "The browser blocked clipboard access. Allow clipboard access for this Studio tab and try again.";
        }
        catch (ArgumentException exception)
        {
            error = $"This node cannot be copied: {exception.Message}";
        }
    }

    private Task PasteClipboardNode(GraphContextRequest context) => PasteClipboardNodeAt(context.Position, context);

    private Task PasteClipboardNodeAt(GraphPoint position) => PasteClipboardNodeAt(position, null);

    private async Task PasteClipboardNodeAt(GraphPoint position, GraphContextRequest? context)
    {
        if (ReadOnly || Disabled) return;
        try
        {
            var fragment = GraphClipboardJson.Deserialize(await JavaScript.InvokeAsync<string>("navigator.clipboard.readText"));
            var inputBindings = fragment.Inputs.Select(boundary =>
            {
                if (boundary.SourceNodeId is null || boundary.SourcePortId is null ||
                    !Definition.Nodes.ContainsKey(boundary.SourceNodeId))
                    throw new ArgumentException("The copied node has an incoming wire that cannot be bound in this workflow.");
                return new GraphClipboardInputBinding(boundary.Id, boundary.SourceNodeId, boundary.SourcePortId);
            }).ToArray();
            var outputBindings = fragment.Outputs.Select(boundary =>
            {
                if (boundary.TargetNodeId is null || boundary.TargetPortId is null ||
                    !Definition.Nodes.ContainsKey(boundary.TargetNodeId))
                    throw new ArgumentException("The copied node has an outgoing wire that cannot be bound in this workflow.");
                return new GraphClipboardOutputBinding(boundary.Id, boundary.TargetNodeId, boundary.TargetPortId);
            }).ToArray();
            var pasted = GraphClipboardEditing.Paste(Definition, EffectiveLayout, fragment, inputBindings, outputBindings, position);
            await CloseMenu(context);
            await CommitEdit(pasted.Definition, pasted.Layout, pasted.NodeIdMap.Values.FirstOrDefault());
            error = null;
        }
        catch (JSException)
        {
            error = "The browser blocked clipboard access. Allow clipboard access for this Studio tab and try again.";
        }
        catch (JsonException)
        {
            error = "The clipboard does not contain a Visual Agent Workflow pure-data node.";
        }
        catch (ArgumentException exception)
        {
            error = $"The node could not be pasted: {exception.Message}";
        }
    }

    private string DataPortType(string nodeId, string portId, WorkflowPortDirection direction) =>
        Definition.Nodes.TryGetValue(nodeId, out var node)
            ? CanvasPorts(node).Single(port => port.Direction == direction && port.Id == portId).DataType ?? "string"
            : "string";

    private static string ClipboardBoundaryId(string prefix, string nodeId, string portId) =>
        $"{prefix}-{SanitizeClipboardId(nodeId)}-{SanitizeClipboardId(portId)}";

    private static string SanitizeClipboardId(string value) =>
        new(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray());

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
        var desired = context?.Position ?? (insertion is not null
            ? new GraphPoint(EffectiveLayout.Nodes[insertion.NodeId].Position.X + 360, EffectiveLayout.Nodes[insertion.NodeId].Position.Y)
            : DefaultNodePosition(presentation, added));
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

    private GraphPoint DefaultNodePosition(WorkflowLayout presentation, string[] added)
    {
        var existing = Definition.Nodes.Keys.Where(id => !added.Contains(id, StringComparer.Ordinal))
            .Select(id => presentation.Nodes[id].Position).ToArray();
        if (existing.Length == 0) return new(320, 240);
        var left = existing.Min(point => point.X);
        var top = existing.Min(point => point.Y);
        var right = existing.Max(point => point.X);
        if (right <= WorkflowLayoutJson.MaximumCoordinate - 720) return new(right + 360, top);
        var bottom = existing.Max(point => point.Y);
        return new(left, Math.Min(bottom + 240, WorkflowLayoutJson.MaximumCoordinate - 320));
    }

    private GraphTemplateDefinition? templateDialog;
    private GraphContextRequest? templatePlacement;
    private readonly Dictionary<string, string> templateInputBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> templateOutputBindings = new(StringComparer.Ordinal);
    private GraphTemplateInsertionResult? templatePreview;
    private string? templatePreviewMessage;

    private static string TemplateKey(GraphTemplateDefinition template) => $"template:{template.TemplateId}:{template.Version}";
    private static string TemplateBindingValue(string templatePortId, string nodeId, string portId) =>
        string.Join('\u001f', templatePortId, nodeId, portId);

    private static bool TryParseTemplateBinding(string value, string expectedPortId, out PortReference reference)
    {
        var parts = value.Split('\u001f');
        if (parts.Length == 3 && parts[0] == expectedPortId && parts[1].Length > 0 && parts[2].Length > 0)
        {
            reference = new(parts[1], parts[2]);
            return true;
        }
        reference = null!;
        return false;
    }

    private void OpenTemplateDialog(string key, GraphContextRequest? context)
    {
        if (TemplateCatalog is null) return;
        var identity = key["template:".Length..];
        var separator = identity.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(identity[(separator + 1)..], out var version)) return;
        var templateId = identity[..separator];
        var entry = TemplateCatalog.Get(templateId, version);
        if (entry is null) return;
        templateDialog = entry.Template.Definition;
        templatePlacement = context;
        templateInputBindings.Clear();
        templateOutputBindings.Clear();
        templatePreview = null;
        templatePreviewMessage = null;
        if (context?.SourceNodeId is { } sourceNodeId && context.SourcePortId is { } sourcePortId &&
            templateDialog.Body.Inputs.Count == 1 &&
            templateDialog.Body.Inputs.Values.Single().DataType == DataTypeOf(sourceNodeId, sourcePortId, output: true))
            templateInputBindings[templateDialog.Body.Inputs.Keys.Single()] = TemplateBindingValue(
                templateDialog.Body.Inputs.Keys.Single(), sourceNodeId, sourcePortId);
        if (context?.TargetNodeId is { } targetNodeId && context.TargetPortId is { } targetPortId &&
            templateDialog.Body.Outputs.Count == 1 &&
            templateDialog.Body.Outputs.Values.Single().DataType == DataTypeOf(targetNodeId, targetPortId, output: false))
            templateOutputBindings[templateDialog.Body.Outputs.Keys.Single()] = TemplateBindingValue(
                templateDialog.Body.Outputs.Keys.Single(), targetNodeId, targetPortId);
        PrepareTemplatePreview();
    }

    private string? DataTypeOf(string nodeId, string portId, bool output)
    {
        if (!Definition.Nodes.TryGetValue(nodeId, out var node)) return null;
        return CanvasPorts(node).FirstOrDefault(port => port.Id == portId && port.Kind == WorkflowPortKind.Data &&
            port.Direction == (output ? WorkflowPortDirection.Output : WorkflowPortDirection.Input))?.DataType;
    }

    private IReadOnlyList<GraphTemplatePortOption> TemplateInputOptions => templateDialog is null ? [] :
        [.. templateDialog.Body.Inputs.SelectMany(input => Definition.Nodes.SelectMany(node =>
            CanvasPorts(node.Value).Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data &&
                port.DataType == input.Value.DataType).Select(port => new GraphTemplatePortOption(
                    TemplateBindingValue(input.Key, node.Key, port.Id), $"{NodeLabel(node.Key)} · {port.Id}"))))];

    private IReadOnlyList<GraphTemplatePortOption> TemplateOutputOptions => templateDialog is null ? [] :
        [.. templateDialog.Body.Outputs.SelectMany(output => Definition.Nodes.SelectMany(node =>
            CanvasPorts(node.Value).Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data &&
                port.DataType == output.Value.DataType && port.Cardinality != WorkflowPortCardinality.Many &&
                !(node.Value.Type == NodeTypes.McpTool && !string.IsNullOrWhiteSpace(node.Value.McpArgumentsJson))).Select(port =>
            {
                var incoming = Definition.Connections.Values.Count(edge => edge.Kind == "data" && edge.Target == new PortReference(node.Key, port.Id));
                return new GraphTemplatePortOption(TemplateBindingValue(output.Key, node.Key, port.Id),
                    $"{NodeLabel(node.Key)} · {port.Id}", incoming == 1);
            })) )];

    private Task TemplateBindingChanged(GraphTemplateBindingChange change)
    {
        var bindings = change.IsInput ? templateInputBindings : templateOutputBindings;
        if (string.IsNullOrEmpty(change.Value)) bindings.Remove(change.TemplatePortId);
        else bindings[change.TemplatePortId] = change.Value;
        PrepareTemplatePreview();
        return Task.CompletedTask;
    }

    private void PrepareTemplatePreview()
    {
        templatePreview = null;
        templatePreviewMessage = null;
        if (templateDialog is null) return;
        if (templateDialog.Body.Inputs.Keys.Any(id => !templateInputBindings.TryGetValue(id, out var value) ||
            !TryParseTemplateBinding(value, id, out _)) ||
            templateDialog.Body.Outputs.Keys.Any(id => !templateOutputBindings.TryGetValue(id, out var value) ||
            !TryParseTemplateBinding(value, id, out _)))
        {
            templatePreviewMessage = "Choose a compatible source and destination for every template boundary.";
            return;
        }
        try
        {
            var request = CreateTemplateInsertionRequest();
            templatePreview = GraphTemplateEditing.Insert(Definition, templateDialog, request,
                _ => "template-node-" + Guid.NewGuid().ToString("N"),
                _ => "template-edge-" + Guid.NewGuid().ToString("N"));
        }
        catch (ArgumentException exception) { templatePreviewMessage = exception.Message; }
    }

    private GraphTemplateInsertionRequest CreateTemplateInsertionRequest() => new(
        [.. templateDialog!.Body.Inputs.Keys.Select(id => new GraphTemplateInputBinding(id,
            TryParseTemplateBinding(templateInputBindings[id], id, out var source) ? source : throw new ArgumentException("Choose every template input binding.")))],
        [.. templateDialog!.Body.Outputs.Keys.Select(id => new GraphTemplateOutputBinding(id,
            TryParseTemplateBinding(templateOutputBindings[id], id, out var target) ? target : throw new ArgumentException("Choose every template output binding.")))]);

    private async Task ApplyTemplate()
    {
        if (templateDialog is null) return;
        try
        {
            var result = GraphTemplateEditing.Insert(Definition, templateDialog, CreateTemplateInsertionRequest(),
                _ => "template-node-" + Guid.NewGuid().ToString("N"),
                _ => "template-edge-" + Guid.NewGuid().ToString("N"));
            var placement = templatePlacement;
            templateDialog = null;
            templatePlacement = null;
            await Edit(() => result.Definition, placement, preserveSelection: result.AddedNodeIds.FirstOrDefault());
        }
        catch (ArgumentException exception) { templatePreviewMessage = exception.Message; }
    }

    private void CancelTemplate()
    {
        templateDialog = null;
        templatePlacement = null;
        templatePreview = null;
        templatePreviewMessage = null;
    }
}
