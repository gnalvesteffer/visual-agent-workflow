using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record GraphFunctionExtractionPlan(
    GraphFunctionConfiguration Function,
    WorkflowDefinition Definition,
    WorkflowLayout Layout,
    WorkflowLayout FunctionLayout,
    string CallNodeId,
    ImmutableArray<string> RemovedNodeIds,
    ImmutableArray<string> RemovedConnectionIds,
    ImmutableArray<string> AddedConnectionIds);

/// <summary>Builds a validated immutable function snapshot and its exact caller replacement without mutating either input.</summary>
public static class GraphFunctionExtraction
{
    private sealed record InputBoundary(string Id, string DataType, PortReference Source,
        ImmutableArray<PortReference> Targets);
    private sealed record OutputBoundary(string Id, string DataType, PortReference Source,
        ImmutableArray<PortReference> Targets);

    public static GraphFunctionExtractionPlan Prepare(WorkflowDefinition source, WorkflowLayout layout,
        IEnumerable<string> selectedNodeIds, string functionId, int version, string displayName,
        string description, string callNodeId, Func<string, string>? connectionIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(selectedNodeIds);
        connectionIdFactory ??= prefix => $"extract-{prefix}-{Guid.NewGuid():N}";

        var selected = selectedNodeIds.ToImmutableHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
            throw new ArgumentException("Select at least one pure data node to extract.", nameof(selectedNodeIds));
        if (selected.Any(id => !source.Nodes.ContainsKey(id)))
            throw new ArgumentException("Every selected node must exist in the workflow.", nameof(selectedNodeIds));
        if (string.IsNullOrWhiteSpace(callNodeId) || callNodeId.Length > GraphFunctionLimits.MaximumIdLength ||
            callNodeId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
            source.Nodes.ContainsKey(callNodeId))
            throw new ArgumentException("The replacement call node ID must be valid and unused.", nameof(callNodeId));

        WorkflowLayoutJson.Validate(layout, source);
        ValidateSelectedNodes(source, selected);

        var incoming = source.Connections.Values
            .Where(edge => !selected.Contains(edge.Source.NodeId) && selected.Contains(edge.Target.NodeId))
            .GroupBy(edge => edge.Source)
            .OrderBy(group => group.Key.NodeId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PortId, StringComparer.Ordinal)
            .Select((group, index) => new InputBoundary(
                $"input-{index + 1}",
                DataType(source, group.Key, WorkflowPortDirection.Output),
                group.Key,
                group.Select(edge => edge.Target).OrderBy(reference => reference.NodeId, StringComparer.Ordinal)
                    .ThenBy(reference => reference.PortId, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();
        var outgoing = source.Connections.Values
            .Where(edge => selected.Contains(edge.Source.NodeId) && !selected.Contains(edge.Target.NodeId))
            .GroupBy(edge => edge.Source)
            .OrderBy(group => group.Key.NodeId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PortId, StringComparer.Ordinal)
            .Select((group, index) => new OutputBoundary(
                $"output-{index + 1}",
                DataType(source, group.Key, WorkflowPortDirection.Output),
                group.Key,
                group.Select(edge => edge.Target).OrderBy(reference => reference.NodeId, StringComparer.Ordinal)
                    .ThenBy(reference => reference.PortId, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();

        if (incoming.IsEmpty || outgoing.IsEmpty)
            throw new ArgumentException("The selected region must have at least one connected input and one connected output.", nameof(selectedNodeIds));
        if (incoming.Length > GraphFunctionLimits.MaximumInputs || outgoing.Length > GraphFunctionLimits.MaximumOutputs)
            throw new ArgumentException("The selected region exceeds the graph function input or output limit.", nameof(selectedNodeIds));

        var function = CreateFunction(source, selected, incoming, outgoing, functionId, version, displayName, description);
        var functionIssues = GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: true, allowPrimitiveArrays: true);
        if (!functionIssues.IsEmpty)
            throw new ArgumentException("The selected region cannot be represented as a graph function: " +
                string.Join("; ", functionIssues.Select(issue => issue.Message)), nameof(selectedNodeIds));

        var configuration = new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function));
        var replacement = Replace(source, layout, selected, incoming, outgoing, configuration, callNodeId, connectionIdFactory);
        var callerIssues = WorkflowValidator.Validate(replacement.Definition);
        if (!callerIssues.IsEmpty)
            throw new ArgumentException("The caller graph is invalid after extraction: " +
                string.Join("; ", callerIssues.Select(issue => issue.Message)), nameof(selectedNodeIds));

        var functionLayout = CreateFunctionLayout(source, layout, function, selected, incoming, outgoing);
        WorkflowLayoutJson.Validate(functionLayout, FunctionDraft(function));
        return new(configuration, replacement.Definition, replacement.Layout, functionLayout, callNodeId,
            selected.Order(StringComparer.Ordinal).ToImmutableArray(), replacement.RemovedConnectionIds,
            replacement.AddedConnectionIds);
    }

    private static void ValidateSelectedNodes(WorkflowDefinition source, ImmutableHashSet<string> selected)
    {
        foreach (var id in selected)
        {
            var node = source.Nodes[id];
            if (!WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) ||
                !contract.IsPure || contract.Effects != WorkflowNodeEffectFlags.None ||
                contract.State != WorkflowNodeStateKind.Stateless || contract.HasDynamicPins || contract.HasExecutionPins)
                throw new ArgumentException($"Node '{id}' is not a supported stateless pure data node.", nameof(selected));
        }

        if ((source.ExecutionConnections?.Values ?? []).Any(edge => selected.Contains(edge.Source.NodeId) || selected.Contains(edge.Target.NodeId)))
            throw new ArgumentException("Selected nodes may not have execution connections.", nameof(selected));
        if (source.Connections.Values.Any(edge => (selected.Contains(edge.Source.NodeId) || selected.Contains(edge.Target.NodeId)) && edge.Kind != "data"))
            throw new ArgumentException("Selected nodes may have data connections only.", nameof(selected));
    }

    private static GraphFunctionDefinition CreateFunction(WorkflowDefinition source, ImmutableHashSet<string> selected,
        ImmutableArray<InputBoundary> inputs, ImmutableArray<OutputBoundary> outputs, string functionId, int version,
        string displayName, string description)
    {
        var nodes = selected.ToImmutableSortedDictionary(id => id, id => source.Nodes[id], StringComparer.Ordinal).ToBuilder();
        var connections = source.Connections
            .Where(pair => selected.Contains(pair.Value.Source.NodeId) && selected.Contains(pair.Value.Target.NodeId))
            .ToImmutableSortedDictionary(StringComparer.Ordinal).ToBuilder();
        var usedNodeIds = nodes.Keys.ToHashSet(StringComparer.Ordinal);
        var usedConnectionIds = connections.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var boundary in inputs)
        {
            var nodeId = UniqueId("function-input-" + boundary.Id, usedNodeIds);
            nodes.Add(nodeId, new(NodeTypes.FunctionInput, "1.0.0")
            {
                FunctionPortId = boundary.Id,
                OutputDataType = boundary.DataType
            });
            foreach (var target in boundary.Targets)
                connections.Add(UniqueId("extract-input-" + boundary.Id, usedConnectionIds),
                    new(new(nodeId, "out"), target, "data"));
        }

        foreach (var boundary in outputs)
        {
            var nodeId = UniqueId("function-output-" + boundary.Id, usedNodeIds);
            nodes.Add(nodeId, new(NodeTypes.FunctionOutput, "1.0.0")
            {
                FunctionPortId = boundary.Id,
                InputDataType = boundary.DataType
            });
            connections.Add(UniqueId("extract-output-" + boundary.Id, usedConnectionIds),
                new(boundary.Source, new(nodeId, "in"), "data"));
        }

        return new(functionId, version, displayName, description,
            inputs.ToImmutableSortedDictionary(boundary => boundary.Id,
                boundary => new GraphFunctionPort(BoundaryDisplayName(boundary.Source), boundary.DataType), StringComparer.Ordinal),
            outputs.ToImmutableSortedDictionary(boundary => boundary.Id,
                boundary => new GraphFunctionPort(BoundaryDisplayName(boundary.Source), boundary.DataType), StringComparer.Ordinal),
            nodes.ToImmutable(), connections.ToImmutable());
    }

    private static (WorkflowDefinition Definition, WorkflowLayout Layout, ImmutableArray<string> RemovedConnectionIds,
        ImmutableArray<string> AddedConnectionIds) Replace(WorkflowDefinition source, WorkflowLayout layout,
        ImmutableHashSet<string> selected, ImmutableArray<InputBoundary> inputs, ImmutableArray<OutputBoundary> outputs,
        GraphFunctionConfiguration function, string callNodeId, Func<string, string> connectionIdFactory)
    {
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0") { GraphFunction = function };
        var nodes = source.Nodes.RemoveRange(selected).Add(callNodeId, call);
        var removedConnections = source.Connections
            .Where(pair => selected.Contains(pair.Value.Source.NodeId) || selected.Contains(pair.Value.Target.NodeId))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToImmutableArray();
        var connections = source.Connections.RemoveRange(removedConnections).ToBuilder();
        var added = ImmutableArray.CreateBuilder<string>();
        var used = connections.Keys.ToHashSet(StringComparer.Ordinal);

        foreach (var boundary in inputs)
            AddConnection("input-" + boundary.Id, new(boundary.Source, new(callNodeId, boundary.Id), "data"));
        foreach (var boundary in outputs)
            for (var index = 0; index < boundary.Targets.Length; index++)
                AddConnection($"output-{boundary.Id}-{index + 1}",
                    new(new(callNodeId, boundary.Id), boundary.Targets[index], "data"));

        var definition = source with { Nodes = nodes, Connections = connections.ToImmutable() };
        var nextLayout = ReplaceLayout(layout, source, definition, selected, callNodeId);
        WorkflowLayoutJson.Validate(nextLayout, definition);
        return (definition, nextLayout, removedConnections, added.ToImmutable());

        void AddConnection(string prefix, ConnectionDefinition connection)
        {
            var id = connectionIdFactory(prefix);
            if (!IsValidId(id) || !used.Add(id))
                throw new ArgumentException($"Connection ID factory produced an invalid or colliding ID '{id}'.", nameof(connectionIdFactory));
            connections.Add(id, connection);
            added.Add(id);
        }
    }

    private static WorkflowLayout ReplaceLayout(WorkflowLayout layout, WorkflowDefinition source,
        WorkflowDefinition definition, ImmutableHashSet<string> selected, string callNodeId)
    {
        var points = selected.Select(id => layout.Nodes[id].Position).ToArray();
        var callPosition = new GraphPoint(points.Average(point => point.X), points.Average(point => point.Y));
        var sections = layout.Sections.ToBuilder();
        foreach (var (id, section) in layout.Sections)
        {
            if (selected.All(section.NodeIds.Contains))
                sections[id] = section with { NodeIds = [.. section.NodeIds.Where(member => !selected.Contains(member)), callNodeId] };
        }
        var prepared = layout with { Sections = sections.ToImmutable() };
        var reconciled = prepared.Reconcile(definition);
        var callLabel = selected.Select(id => layout.Nodes[id].Label).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        return reconciled with
        {
            Nodes = reconciled.Nodes.SetItem(callNodeId, reconciled.Nodes[callNodeId] with
            {
                Position = callPosition,
                Label = callLabel
            })
        };
    }

    private static WorkflowLayout CreateFunctionLayout(WorkflowDefinition source, WorkflowLayout sourceLayout,
        GraphFunctionDefinition function, ImmutableHashSet<string> selected, ImmutableArray<InputBoundary> inputs,
        ImmutableArray<OutputBoundary> outputs)
    {
        var draft = FunctionDraft(function);
        var layout = WorkflowLayout.Create(draft);
        var selectedPoints = selected.Select(id => sourceLayout.Nodes[id].Position).ToArray();
        var minX = selectedPoints.Min(point => point.X);
        var maxX = selectedPoints.Max(point => point.X);
        var minY = selectedPoints.Min(point => point.Y);
        var maxY = selectedPoints.Max(point => point.Y);
        var availableWidth = WorkflowLayoutJson.MaximumCoordinate - 800;
        var availableHeight = WorkflowLayoutJson.MaximumCoordinate - 400;
        var scale = Math.Min(1, Math.Min(availableWidth / Math.Max(1, maxX - minX), availableHeight / Math.Max(1, maxY - minY)));
        var translated = layout.Nodes.ToBuilder();
        foreach (var id in selected)
        {
            var sourcePresentation = sourceLayout.Nodes[id];
            var sourcePoint = sourcePresentation.Position;
            translated[id] = sourcePresentation with
            {
                Position = new(400 + (sourcePoint.X - minX) * scale, 160 + (sourcePoint.Y - minY) * scale)
            };
        }

        var inputNodeIds = function.Nodes.Where(pair => pair.Value.Type == NodeTypes.FunctionInput)
            .ToDictionary(pair => pair.Value.FunctionPortId!, pair => pair.Key, StringComparer.Ordinal);
        var outputNodeIds = function.Nodes.Where(pair => pair.Value.Type == NodeTypes.FunctionOutput)
            .ToDictionary(pair => pair.Value.FunctionPortId!, pair => pair.Key, StringComparer.Ordinal);
        for (var index = 0; index < inputs.Length; index++)
            translated[inputNodeIds[inputs[index].Id]] = translated[inputNodeIds[inputs[index].Id]] with
                { Position = new(80, 160 + index * 160) };
        var outputX = Math.Min(WorkflowLayoutJson.MaximumCoordinate - 240, 760 + (maxX - minX) * scale);
        for (var index = 0; index < outputs.Length; index++)
            translated[outputNodeIds[outputs[index].Id]] = translated[outputNodeIds[outputs[index].Id]] with
                { Position = new(outputX, 160 + index * 160) };

        var wires = ImmutableSortedDictionary.CreateBuilder<string, WirePresentation>(StringComparer.Ordinal);
        foreach (var (id, edge) in function.Connections)
        {
            var sourceId = selected.Contains(edge.Source.NodeId) ? edge.Source.NodeId : null;
            var targetId = selected.Contains(edge.Target.NodeId) ? edge.Target.NodeId : null;
            var oldId = sourceId is not null && targetId is not null
                ? source.Connections.Values.FirstOrDefault(candidate => candidate.Source == edge.Source && candidate.Target == edge.Target) is { } sourceEdge
                    ? source.Connections.Single(pair => pair.Value == sourceEdge).Key : null
                : null;
            if (oldId is not null && sourceLayout.Wires.TryGetValue(oldId, out var route)) wires[id] = route;
            else if (oldId is not null && sourceLayout.Wires.TryGetValue("data:" + oldId, out route)) wires[id] = route;
        }
        var sections = sourceLayout.Sections.Values
            .Select(section => section with { NodeIds = [.. section.NodeIds.Where(selected.Contains)] })
            .Where(section => !section.NodeIds.IsEmpty)
            .Select((section, index) => KeyValuePair.Create($"extracted-section-{index + 1}", section))
            .ToImmutableSortedDictionary(StringComparer.Ordinal);
        return layout with { Nodes = translated.ToImmutable(), Wires = wires.ToImmutable(), Sections = sections };
    }

    private static WorkflowDefinition FunctionDraft(GraphFunctionDefinition function) => new(
        WorkflowSchemaVersions.PureGraphFunctions,
        "graph-function-layout",
        function.Nodes.Keys.First(),
        function.Nodes,
        function.Connections);

    private static string DataType(WorkflowDefinition source, PortReference reference, WorkflowPortDirection direction)
    {
        if (!source.Nodes.TryGetValue(reference.NodeId, out var node))
            throw new ArgumentException($"Boundary node '{reference.NodeId}' is missing.");
        var port = WorkflowPorts.Explicit(node).SingleOrDefault(candidate => candidate.Id == reference.PortId &&
            candidate.Kind == WorkflowPortKind.Data && candidate.Direction == direction);
        return port?.DataType ?? throw new ArgumentException($"Boundary port '{reference.NodeId}.{reference.PortId}' is not a declared data pin.");
    }

    private static string UniqueId(string baseId, HashSet<string> used)
    {
        var candidate = baseId;
        var suffix = 1;
        while (!used.Add(candidate)) candidate = $"{baseId}-{suffix++}";
        if (!IsValidId(candidate)) throw new ArgumentException("Generated graph function boundary ID is invalid.");
        return candidate;
    }

    private static bool IsValidId(string id) => id.Length is > 0 and <= GraphFunctionLimits.MaximumIdLength &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string BoundaryDisplayName(PortReference source)
    {
        var displayName = $"{source.NodeId} · {source.PortId}";
        return displayName.Length <= GraphFunctionLimits.MaximumDisplayNameLength
            ? displayName
            : displayName[..GraphFunctionLimits.MaximumDisplayNameLength];
    }
}
