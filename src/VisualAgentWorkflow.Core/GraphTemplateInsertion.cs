using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Connects one declared pure-template input to an existing workflow data output.</summary>
public sealed record GraphTemplateInputBinding(string TemplatePortId, PortReference Source);

/// <summary>Connects one declared pure-template output to an existing workflow data input.</summary>
public sealed record GraphTemplateOutputBinding(string TemplatePortId, PortReference Target);

public sealed record GraphTemplateInsertionRequest(
    ImmutableArray<GraphTemplateInputBinding> Inputs,
    ImmutableArray<GraphTemplateOutputBinding> Outputs);

/// <summary>Immutable delta returned by pure-template insertion; suitable for one editor history entry.</summary>
public sealed record GraphTemplateInsertionResult(
    WorkflowDefinition Definition,
    ImmutableArray<string> AddedNodeIds,
    ImmutableArray<string> AddedConnectionIds,
    ImmutableArray<string> RemovedConnectionIds,
    ImmutableSortedDictionary<string, ConnectionDefinition> RemovedConnections,
    ImmutableSortedDictionary<string, string> TemplateNodeMap,
    ImmutableSortedDictionary<string, string> TemplateConnectionMap);

/// <summary>Copies a validated pure template into a workflow and rewires only its explicit boundaries.</summary>
public static class GraphTemplateEditing
{
    public static GraphTemplateInsertionResult Insert(
        WorkflowDefinition target,
        GraphTemplateDefinition template,
        GraphTemplateInsertionRequest request,
        Func<string, string>? nodeIdFactory = null,
        Func<string, string>? connectionIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(request);
        nodeIdFactory ??= source => "template-node-" + source;
        connectionIdFactory ??= source => "template-connection-" + source;

        RequireValid("target", WorkflowValidator.Validate(target));
        RequireValid("template", GraphTemplateJson.Validate(template));
        ValidateRequest(request, template);

        var inputBindings = request.Inputs.ToDictionary(binding => binding.TemplatePortId, StringComparer.Ordinal);
        var outputBindings = request.Outputs.ToDictionary(binding => binding.TemplatePortId, StringComparer.Ordinal);
        var inputBoundaries = BoundaryNodes(template, NodeTypes.FunctionInput);
        var outputBoundaries = BoundaryNodes(template, NodeTypes.FunctionOutput);
        var errors = new List<string>();

        foreach (var (boundaryId, portId) in inputBoundaries)
        {
            var binding = inputBindings[portId];
            ValidateSourceBinding(target, binding.Source, template.Body.Inputs[portId].DataType, errors);
            if (!template.Body.Connections.Values.Any(edge => edge.Source.NodeId == boundaryId))
                errors.Add($"Template input '{portId}' has no outgoing boundary edge.");
        }
        foreach (var (boundaryId, portId) in outputBoundaries)
        {
            var binding = outputBindings[portId];
            ValidateTargetBinding(target, binding.Target, template.Body.Outputs[portId].DataType, errors);
            if (template.Body.Connections.Values.Count(edge => edge.Target.NodeId == boundaryId) != 1)
                errors.Add($"Template output '{portId}' must have exactly one incoming boundary edge.");
        }

        var targetInputEdges = new Dictionary<PortReference, string>();
        foreach (var (portId, binding) in outputBindings)
        {
            if (!target.Nodes.TryGetValue(binding.Target.NodeId, out var targetNode) || targetNode is null)
                continue;
            var descriptor = WorkflowPorts.Explicit(targetNode).SingleOrDefault(port => port.Id == binding.Target.PortId &&
                port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data);
            if (descriptor is null)
                continue;
            if (descriptor.Cardinality == WorkflowPortCardinality.Many)
                errors.Add($"Template output '{portId}' cannot bind to a many-cardinality input.");
            if (descriptor.SupportsStaticValue && HasStaticValue(targetNode))
                errors.Add($"Template output '{portId}' cannot replace a statically configured input.");

            var incoming = target.Connections.Where(pair => pair.Value.Kind == "data" && pair.Value.Target == binding.Target).ToArray();
            if (incoming.Length > 1)
                errors.Add($"Target input '{binding.Target.NodeId}.{binding.Target.PortId}' has multiple incoming data edges.");
            else if (incoming.Length == 1)
                targetInputEdges[binding.Target] = incoming[0].Key;
        }
        foreach (var group in outputBindings.Values.GroupBy(binding => binding.Target))
            if (group.Count() > 1)
                errors.Add($"Target input '{group.Key.NodeId}.{group.Key.PortId}' may be bound by only one template output.");

        var mappedNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNodeIds = target.Nodes.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (sourceId, node) in template.Body.Nodes)
        {
            if (node.Type is NodeTypes.FunctionInput or NodeTypes.FunctionOutput)
                continue;
            var mapped = nodeIdFactory(sourceId);
            if (!IsValidIdentifier(mapped) || !usedNodeIds.Add(mapped))
                errors.Add($"Node ID factory produced an invalid or colliding ID '{mapped}'.");
            else
                mappedNodes[sourceId] = mapped;
        }

        var usedConnectionIds = target.Connections.Keys.Concat(target.ExecutionConnections?.Keys ?? []).ToHashSet(StringComparer.Ordinal);
        var translated = new List<(string SourceTemplateEdgeId, ConnectionDefinition Definition, string Id)>();
        foreach (var (edgeId, edge) in template.Body.Connections)
        {
            var source = TranslateSourceEndpoint(edge.Source, inputBoundaries, inputBindings, mappedNodes, errors);
            var destination = TranslateTargetEndpoint(edge.Target, outputBoundaries, outputBindings, mappedNodes, errors);
            if (source is null || destination is null)
                continue;
            if (source == destination)
                errors.Add($"Template edge '{edgeId}' would connect a pin to itself.");
            var id = connectionIdFactory(edgeId);
            if (!IsValidIdentifier(id) || !usedConnectionIds.Add(id))
                errors.Add($"Connection ID factory produced an invalid or colliding ID '{id}'.");
            else
                translated.Add((edgeId, new ConnectionDefinition(source, destination, "data"), id));
        }

        if (target.Nodes.Count + mappedNodes.Count > 256)
            errors.Add("The inserted template would exceed the workflow node limit of 256.");
        var removedCount = targetInputEdges.Count;
        var connectionLimit = WorkflowSchemaVersions.UsesExplicitGraph(target) ? 510 : 255;
        if (target.Connections.Count - removedCount + translated.Count > connectionLimit)
            errors.Add($"The inserted template would exceed the workflow connection limit of {connectionLimit}.");
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors), nameof(request));

        var nodes = target.Nodes.ToBuilder();
        foreach (var sourceId in mappedNodes.Keys)
            nodes.Add(mappedNodes[sourceId], template.Body.Nodes[sourceId]);
        var connections = target.Connections.ToBuilder();
        var removed = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        foreach (var edgeId in targetInputEdges.Values)
        {
            removed.Add(edgeId, connections[edgeId]);
            connections.Remove(edgeId);
        }
        var addedConnectionIds = ImmutableArray.CreateBuilder<string>();
        var connectionMap = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var item in translated)
        {
            connections.Add(item.Id, item.Definition);
            addedConnectionIds.Add(item.Id);
            connectionMap.Add(item.SourceTemplateEdgeId, item.Id);
        }

        var candidate = target with { Nodes = nodes.ToImmutable(), Connections = connections.ToImmutable() };
        RequireValid("inserted workflow", WorkflowValidator.Validate(candidate));
        return new(candidate,
            mappedNodes.Values.Order(StringComparer.Ordinal).ToImmutableArray(),
            addedConnectionIds.ToImmutable(),
            removed.Keys.ToImmutableArray(),
            removed.ToImmutable(),
            mappedNodes.ToImmutableSortedDictionary(StringComparer.Ordinal),
            connectionMap.ToImmutable());
    }

    private static void ValidateRequest(GraphTemplateInsertionRequest request, GraphTemplateDefinition template)
    {
        if (request.Inputs.IsDefault || request.Outputs.IsDefault)
            throw new ArgumentException("Template bindings must be initialized.", nameof(request));
        var inputIds = request.Inputs.Select(binding => binding.TemplatePortId).ToArray();
        var outputIds = request.Outputs.Select(binding => binding.TemplatePortId).ToArray();
        if (inputIds.Length != template.Body.Inputs.Count || inputIds.Distinct(StringComparer.Ordinal).Count() != inputIds.Length ||
            inputIds.Any(id => !template.Body.Inputs.ContainsKey(id)))
            throw new ArgumentException("Template inputs must be bound exactly once.", nameof(request));
        if (outputIds.Length != template.Body.Outputs.Count || outputIds.Distinct(StringComparer.Ordinal).Count() != outputIds.Length ||
            outputIds.Any(id => !template.Body.Outputs.ContainsKey(id)))
            throw new ArgumentException("Template outputs must be bound exactly once.", nameof(request));
        if (request.Inputs.Any(binding => binding.Source is null) || request.Outputs.Any(binding => binding.Target is null))
            throw new ArgumentException("Template bindings require non-null port references.", nameof(request));
    }

    private static void ValidateSourceBinding(WorkflowDefinition target, PortReference source, string dataType, List<string> errors)
    {
        if (!target.Nodes.TryGetValue(source.NodeId, out var node) || node is null)
        {
            errors.Add($"Template input source node '{source.NodeId}' does not exist.");
            return;
        }
        var port = WorkflowPorts.Explicit(node).SingleOrDefault(candidate => candidate.Id == source.PortId &&
            candidate.Direction == WorkflowPortDirection.Output && candidate.Kind == WorkflowPortKind.Data);
        if (port is null || port.DataType != dataType)
            errors.Add($"Template input source '{source.NodeId}.{source.PortId}' must be a data output of type '{dataType}'.");
    }

    private static void ValidateTargetBinding(WorkflowDefinition target, PortReference destination, string dataType, List<string> errors)
    {
        if (!target.Nodes.TryGetValue(destination.NodeId, out var node) || node is null)
        {
            errors.Add($"Template output target node '{destination.NodeId}' does not exist.");
            return;
        }
        var port = WorkflowPorts.Explicit(node).SingleOrDefault(candidate => candidate.Id == destination.PortId &&
            candidate.Direction == WorkflowPortDirection.Input && candidate.Kind == WorkflowPortKind.Data);
        if (port is null || port.DataType != dataType)
            errors.Add($"Template output target '{destination.NodeId}.{destination.PortId}' must be a data input of type '{dataType}'.");
    }

    private static PortReference? TranslateSourceEndpoint(PortReference endpoint,
        IReadOnlyDictionary<string, string> boundaries,
        IReadOnlyDictionary<string, GraphTemplateInputBinding> boundaryBindings,
        IReadOnlyDictionary<string, string> mappedNodes,
        List<string> errors)
    {
        if (boundaries.TryGetValue(endpoint.NodeId, out var portId))
        {
            if (boundaryBindings.TryGetValue(portId, out var inputBinding))
                return inputBinding.Source;
            errors.Add($"Template input boundary '{portId}' is missing a binding.");
            return null;
        }
        return mappedNodes.TryGetValue(endpoint.NodeId, out var mapped)
            ? new PortReference(mapped, endpoint.PortId)
            : MissingEndpoint(endpoint, errors);
    }

    private static PortReference? TranslateTargetEndpoint(PortReference endpoint,
        IReadOnlyDictionary<string, string> boundaries,
        IReadOnlyDictionary<string, GraphTemplateOutputBinding> boundaryBindings,
        IReadOnlyDictionary<string, string> mappedNodes,
        List<string> errors)
    {
        if (boundaries.TryGetValue(endpoint.NodeId, out var portId))
        {
            if (boundaryBindings.TryGetValue(portId, out var outputBinding))
                return outputBinding.Target;
            errors.Add($"Template output boundary '{portId}' is missing a binding.");
            return null;
        }
        return mappedNodes.TryGetValue(endpoint.NodeId, out var mapped)
            ? new PortReference(mapped, endpoint.PortId)
            : MissingEndpoint(endpoint, errors);
    }

    private static PortReference? MissingEndpoint(PortReference endpoint, List<string> errors)
    {
        errors.Add($"Template edge references an unknown node '{endpoint.NodeId}'.");
        return null;
    }

    private static IReadOnlyDictionary<string, string> BoundaryNodes(GraphTemplateDefinition template, string nodeType) =>
        template.Body.Nodes.Where(pair => pair.Value.Type == nodeType && pair.Value.FunctionPortId is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.FunctionPortId!, StringComparer.Ordinal);

    private static bool HasStaticValue(NodeDefinition node) => node.Type == NodeTypes.McpTool && !string.IsNullOrWhiteSpace(node.McpArgumentsJson);

    private static bool IsValidIdentifier(string? value) => value is { Length: > 0 and <= GraphFunctionLimits.MaximumIdLength } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void RequireValid(string name, ImmutableArray<ValidationIssue> issues)
    {
        if (issues.Length > 0)
            throw new ArgumentException($"The {name} is invalid: {string.Join("; ", issues.Select(issue => issue.Message))}", name);
    }
}
