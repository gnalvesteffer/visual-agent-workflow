using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Authoring-only clipboard snapshot for a bounded pure data subgraph.</summary>
public sealed record GraphClipboardFragment(
    string SourceSchemaVersion,
    ImmutableSortedDictionary<string, NodeDefinition> Nodes,
    ImmutableSortedDictionary<string, ConnectionDefinition> Connections,
    ImmutableSortedDictionary<string, GraphClipboardNodePresentation> Layout,
    ImmutableArray<GraphClipboardInputBoundary> Inputs,
    ImmutableArray<GraphClipboardOutputBoundary> Outputs);

public sealed record GraphClipboardNodePresentation(GraphPoint Position);

public sealed record GraphClipboardInputBoundary(string Id, string DataType, string TargetNodeId, string TargetPortId);
public sealed record GraphClipboardOutputBoundary(string Id, string DataType, string SourceNodeId, string SourcePortId);

public sealed record GraphClipboardDocument(string FormatVersion, GraphClipboardFragment Fragment, string ContentHash);

public sealed record GraphClipboardInputBinding(string BoundaryId, string SourceNodeId, string SourcePortId);
public sealed record GraphClipboardOutputBinding(string BoundaryId, string TargetNodeId, string TargetPortId);

public sealed record GraphClipboardPasteResult(
    WorkflowDefinition Definition,
    WorkflowLayout Layout,
    ImmutableSortedDictionary<string, string> NodeIdMap,
    ImmutableSortedDictionary<string, string> ConnectionIdMap);

public static class GraphClipboardLimits
{
    public const string CurrentFormatVersion = "1.0";
    public const int MaximumJsonBytes = 1_048_576;
    public const int MaximumNodes = 128;
    public const int MaximumConnections = 256;
    public const int MaximumBoundaries = 64;
}

/// <summary>Strict serialization and validation for transient, authoring-only clipboard fragments.</summary>
public static class GraphClipboardJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static string ComputeHash(GraphClipboardFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        Validate(fragment);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeFragment(fragment))));
    }

    public static string Serialize(GraphClipboardFragment fragment)
    {
        Validate(fragment);
        var document = new GraphClipboardDocument(GraphClipboardLimits.CurrentFormatVersion, fragment, ComputeHash(fragment));
        var json = JsonSerializer.Serialize(document, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(json) > GraphClipboardLimits.MaximumJsonBytes)
            throw new ArgumentException("The graph clipboard fragment exceeds the 1 MiB limit.", nameof(fragment));
        return json;
    }

    public static GraphClipboardFragment Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > GraphClipboardLimits.MaximumJsonBytes)
            throw new ArgumentException("The graph clipboard fragment exceeds the 1 MiB limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var value = JsonSerializer.Deserialize<GraphClipboardDocument>(json, Options)
            ?? throw new JsonException("A graph clipboard document is required.");
        if (value.FormatVersion != GraphClipboardLimits.CurrentFormatVersion || value.Fragment is null ||
            !string.Equals(value.ContentHash, ComputeHash(value.Fragment), StringComparison.Ordinal))
            throw new JsonException("The graph clipboard format or content hash is invalid.");
        return value.Fragment;
    }

    private static string SerializeFragment(GraphClipboardFragment fragment)
    {
        var normalized = fragment with
        {
            Nodes = fragment.Nodes.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Connections = fragment.Connections.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Layout = fragment.Layout.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Inputs = [.. fragment.Inputs.OrderBy(boundary => boundary.Id, StringComparer.Ordinal)],
            Outputs = [.. fragment.Outputs.OrderBy(boundary => boundary.Id, StringComparer.Ordinal)]
        };
        return JsonSerializer.Serialize(normalized, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void Validate(GraphClipboardFragment fragment)
    {
        var issues = GraphClipboardEditing.Validate(fragment);
        if (!issues.IsEmpty) throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(fragment));
    }
}

/// <summary>Extracts and pastes the intentionally narrow pure-data clipboard contract.</summary>
public static class GraphClipboardEditing
{
    public static ImmutableArray<ValidationIssue> Validate(GraphClipboardFragment? fragment)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));
        if (fragment is null)
        {
            Error("clipboard.missing", "fragment", "A graph clipboard fragment is required.");
            return issues.ToImmutable();
        }
        if (!WorkflowSchemaCatalog.IsSupported(fragment.SourceSchemaVersion))
            Error("clipboard.schema", "sourceSchemaVersion", "The clipboard source schema is not supported.");
        if (fragment.Nodes is null || fragment.Nodes.Count is < 1 or > GraphClipboardLimits.MaximumNodes)
            Error("clipboard.nodes.limit", "nodes", $"A clipboard fragment contains 1–{GraphClipboardLimits.MaximumNodes} nodes.");
        if (fragment.Connections is null || fragment.Connections.Count > GraphClipboardLimits.MaximumConnections)
            Error("clipboard.connections.limit", "connections", $"A clipboard fragment contains at most {GraphClipboardLimits.MaximumConnections} data connections.");
        if (fragment.Layout is null || fragment.Layout.Count != (fragment.Nodes?.Count ?? -1) ||
            fragment.Nodes is null || fragment.Layout.Keys.Except(fragment.Nodes.Keys, StringComparer.Ordinal).Any())
            Error("clipboard.layout", "layout", "Clipboard layout must contain exactly one position for every node.");
        if (fragment.Inputs.IsDefault || fragment.Outputs.IsDefault ||
            fragment.Inputs.Length > GraphClipboardLimits.MaximumBoundaries || fragment.Outputs.Length > GraphClipboardLimits.MaximumBoundaries)
            Error("clipboard.boundaries.limit", "boundaries", $"A clipboard fragment contains at most {GraphClipboardLimits.MaximumBoundaries} boundaries per direction.");
        if (fragment.Nodes is null || fragment.Connections is null || fragment.Layout is null)
            return issues.ToImmutable();
        foreach (var (id, presentation) in fragment.Layout)
            if (presentation is null || !double.IsFinite(presentation.Position.X) || !double.IsFinite(presentation.Position.Y) ||
                presentation.Position.X < 0 || presentation.Position.Y < 0 ||
                presentation.Position.X > WorkflowLayoutJson.MaximumCoordinate || presentation.Position.Y > WorkflowLayoutJson.MaximumCoordinate)
                Error("clipboard.layout.position", $"layout.{id}", "Clipboard node positions must be finite and within the layout bounds.");

        foreach (var (id, node) in fragment.Nodes)
            {
                if (!ValidId(id)) Error("clipboard.node.id", $"nodes.{id}", "Clipboard node IDs must be bounded portable identifiers.");
                if (node is null || !WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) ||
                    !string.Equals(node.TypeVersion, contract.TypeVersion, StringComparison.Ordinal))
                {
                    Error("clipboard.node.unsupported", $"nodes.{id}", "Clipboard nodes must use the current supported node contract.");
                    continue;
                }
                if (!contract.IsPure || contract.Effects != WorkflowNodeEffectFlags.None ||
                    contract.State != WorkflowNodeStateKind.Stateless || contract.HasDynamicPins || contract.HasExecutionPins ||
                    node.Origin is not null)
                    Error("clipboard.node.ineligible", $"nodes.{id}", "Only current, stateless, pure data nodes without dynamic or execution pins may be copied.");
            }

        foreach (var (id, edge) in fragment.Connections)
            {
                if (!ValidId(id)) Error("clipboard.connection.id", $"connections.{id}", "Clipboard connection IDs must be bounded portable identifiers.");
                if (edge is null || edge.Kind != "data" || !fragment.Nodes.ContainsKey(edge.Source.NodeId) || !fragment.Nodes.ContainsKey(edge.Target.NodeId))
                {
                    Error("clipboard.connection", $"connections.{id}", "Clipboard connections must be internal data edges.");
                    continue;
                }
                var source = WorkflowPorts.ExplicitDataOutputs(fragment.Nodes[edge.Source.NodeId]).SingleOrDefault(port => port.Id == edge.Source.PortId);
                var target = WorkflowPorts.ExplicitDataInputs(fragment.Nodes[edge.Target.NodeId]).SingleOrDefault(port => port.Id == edge.Target.PortId);
                if (source is null || target is null || source.DataType != target.DataType)
                    Error("clipboard.connection.port", $"connections.{id}", "Clipboard connections must join matching typed data ports.");
            }

        var inputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in fragment.Inputs.IsDefault ? [] : fragment.Inputs)
        {
            if (boundary is null || !ValidId(boundary.Id) || !inputIds.Add(boundary.Id) || !ValidType(boundary.DataType) ||
                !fragment.Nodes.ContainsKey(boundary.TargetNodeId))
            {
                Error("clipboard.input", "inputs", "Each clipboard input boundary must be unique, typed, and target a copied node.");
                continue;
            }
            var port = WorkflowPorts.ExplicitDataInputs(fragment.Nodes[boundary.TargetNodeId]).SingleOrDefault(candidate => candidate.Id == boundary.TargetPortId);
            if (port is null || port.DataType != boundary.DataType)
                Error("clipboard.input.port", $"inputs.{boundary.Id}", "Clipboard input boundaries must match copied data input ports.");
        }
        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in fragment.Outputs.IsDefault ? [] : fragment.Outputs)
        {
            if (boundary is null || !ValidId(boundary.Id) || !outputIds.Add(boundary.Id) || !ValidType(boundary.DataType) ||
                !fragment.Nodes.ContainsKey(boundary.SourceNodeId))
            {
                Error("clipboard.output", "outputs", "Each clipboard output boundary must be unique, typed, and source a copied node.");
                continue;
            }
            var port = WorkflowPorts.ExplicitDataOutputs(fragment.Nodes[boundary.SourceNodeId]).SingleOrDefault(candidate => candidate.Id == boundary.SourcePortId);
            if (port is null || port.DataType != boundary.DataType)
                Error("clipboard.output.port", $"outputs.{boundary.Id}", "Clipboard output boundaries must match copied data output ports.");
        }
        return issues.ToImmutable();
    }

    public static GraphClipboardFragment Extract(WorkflowDefinition source, WorkflowLayout layout, IEnumerable<string> nodeIds,
        IEnumerable<GraphClipboardInputBoundary> inputs, IEnumerable<GraphClipboardOutputBoundary> outputs)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        WorkflowValidator.ThrowIfInvalid(source);
        WorkflowLayoutJson.Validate(layout, source);
        var selected = nodeIds.ToImmutableHashSet(StringComparer.Ordinal);
        var inputBoundaries = inputs.ToImmutableArray();
        var outputBoundaries = outputs.ToImmutableArray();
        if (selected.Count == 0 || selected.Any(id => !source.Nodes.ContainsKey(id)))
            throw new ArgumentException("Select one or more existing nodes.", nameof(nodeIds));
        if (source.ExecutionConnections?.Values.Any(edge => selected.Contains(edge.Source.NodeId) || selected.Contains(edge.Target.NodeId)) == true)
            throw new ArgumentException("Clipboard selection cannot contain or touch execution wires.", nameof(nodeIds));

        foreach (var edge in source.Connections.Values.Where(edge => selected.Contains(edge.Source.NodeId) || selected.Contains(edge.Target.NodeId)))
        {
            var crossesIn = !selected.Contains(edge.Source.NodeId) && selected.Contains(edge.Target.NodeId);
            var crossesOut = selected.Contains(edge.Source.NodeId) && !selected.Contains(edge.Target.NodeId);
            if (crossesIn && !inputBoundaries.Any(boundary => boundary.TargetNodeId == edge.Target.NodeId && boundary.TargetPortId == edge.Target.PortId))
                throw new ArgumentException("Every incoming selected data edge must have an explicit clipboard input boundary.", nameof(inputs));
            if (crossesOut && !outputBoundaries.Any(boundary => boundary.SourceNodeId == edge.Source.NodeId && boundary.SourcePortId == edge.Source.PortId))
                throw new ArgumentException("Every outgoing selected data edge must have an explicit clipboard output boundary.", nameof(outputs));
        }
        var incomingBoundaryKeys = inputBoundaries.Select(boundary => (boundary.TargetNodeId, boundary.TargetPortId)).ToHashSet();
        var outgoingBoundaryKeys = outputBoundaries.Select(boundary => (boundary.SourceNodeId, boundary.SourcePortId)).ToHashSet();
        var incomingCrossings = source.Connections.Values.Where(edge => !selected.Contains(edge.Source.NodeId) && selected.Contains(edge.Target.NodeId))
            .Select(edge => (edge.Target.NodeId, edge.Target.PortId)).ToHashSet();
        var outgoingCrossings = source.Connections.Values.Where(edge => selected.Contains(edge.Source.NodeId) && !selected.Contains(edge.Target.NodeId))
            .Select(edge => (edge.Source.NodeId, edge.Source.PortId)).ToHashSet();
        if (!incomingBoundaryKeys.SetEquals(incomingCrossings) || !outgoingBoundaryKeys.SetEquals(outgoingCrossings))
            throw new ArgumentException("Clipboard boundaries must exactly describe the selected subgraph's crossing data edges.", nameof(inputs));
        var fragment = new GraphClipboardFragment(source.SchemaVersion,
            selected.ToImmutableSortedDictionary(id => id, id => source.Nodes[id] with { Origin = null }, StringComparer.Ordinal),
            source.Connections.Where(pair => selected.Contains(pair.Value.Source.NodeId) && selected.Contains(pair.Value.Target.NodeId))
                .ToImmutableSortedDictionary(StringComparer.Ordinal),
            selected.ToImmutableSortedDictionary(id => id, id => new GraphClipboardNodePresentation(layout.Nodes[id].Position), StringComparer.Ordinal),
            inputBoundaries, outputBoundaries);
        var issues = Validate(fragment);
        if (!issues.IsEmpty) throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(nodeIds));
        return fragment;
    }

    public static GraphClipboardPasteResult Paste(WorkflowDefinition target, WorkflowLayout layout, GraphClipboardFragment fragment,
        IEnumerable<GraphClipboardInputBinding> inputs, IEnumerable<GraphClipboardOutputBinding> outputs, GraphPoint placement,
        Func<string, string>? nodeIdFactory = null, Func<string, string>? connectionIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (!WorkflowSchemaVersions.UsesExplicitGraph(target) || !double.IsFinite(placement.X) || !double.IsFinite(placement.Y))
            throw new ArgumentException("Clipboard paste requires an explicit graph and a finite placement.", nameof(target));
        WorkflowValidator.ThrowIfInvalid(target);
        WorkflowLayoutJson.Validate(layout, target);
        var issues = Validate(fragment);
        if (!issues.IsEmpty) throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(fragment));
        var inputBindings = inputs.ToImmutableArray();
        var outputBindings = outputs.ToImmutableArray();
        var inputMap = fragment.Inputs.ToDictionary(boundary => boundary.Id, StringComparer.Ordinal);
        var outputMap = fragment.Outputs.ToDictionary(boundary => boundary.Id, StringComparer.Ordinal);
        if (inputBindings.Select(binding => binding.BoundaryId).Distinct(StringComparer.Ordinal).Count() != inputBindings.Length ||
            outputBindings.Select(binding => binding.BoundaryId).Distinct(StringComparer.Ordinal).Count() != outputBindings.Length ||
            inputBindings.Any(binding => !inputMap.ContainsKey(binding.BoundaryId)) || outputBindings.Any(binding => !outputMap.ContainsKey(binding.BoundaryId)))
            throw new ArgumentException("Paste bindings must name each existing clipboard boundary at most once.", nameof(inputs));

        nodeIdFactory ??= sourceId => "paste-node-" + Guid.NewGuid().ToString("N");
        connectionIdFactory ??= sourceId => "paste-link-" + Guid.NewGuid().ToString("N");
        var nodeMap = fragment.Nodes.Keys.ToImmutableSortedDictionary(id => id, id => nodeIdFactory(id), StringComparer.Ordinal);
        if (nodeMap.Values.Any(id => !ValidId(id)) || nodeMap.Values.Distinct(StringComparer.Ordinal).Count() != nodeMap.Count ||
            nodeMap.Values.Any(target.Nodes.ContainsKey))
            throw new ArgumentException("The node ID factory must return unique IDs not present in the target.", nameof(nodeIdFactory));
        var connectionMap = fragment.Connections.Keys.ToImmutableSortedDictionary(id => id, id => connectionIdFactory(id), StringComparer.Ordinal);
        var allConnectionIds = (target.Connections.Keys.Concat(target.ExecutionConnections?.Keys ?? [])).ToHashSet(StringComparer.Ordinal);
        if (connectionMap.Values.Any(id => !ValidId(id)) || connectionMap.Values.Distinct(StringComparer.Ordinal).Count() != connectionMap.Count ||
            connectionMap.Values.Any(allConnectionIds.Contains))
            throw new ArgumentException("The connection ID factory must return unique IDs not present in the target.", nameof(connectionIdFactory));

        var targetPorts = WorkflowPortsForTarget(target);
        foreach (var binding in inputBindings)
        {
            var boundary = inputMap[binding.BoundaryId];
            var sourcePort = targetPorts(binding.SourceNodeId, binding.SourcePortId, true);
            if (sourcePort is null || sourcePort.DataType != boundary.DataType)
                throw new ArgumentException("Clipboard input bindings must match typed target output ports.", nameof(inputs));
        }
        foreach (var binding in outputBindings)
        {
            var boundary = outputMap[binding.BoundaryId];
            var targetPort = targetPorts(binding.TargetNodeId, binding.TargetPortId, false);
            if (targetPort is null || targetPort.DataType != boundary.DataType)
                throw new ArgumentException("Clipboard output bindings must match typed target input ports.", nameof(outputs));
        }

        var minX = fragment.Layout.Values.Min(value => value.Position.X);
        var minY = fragment.Layout.Values.Min(value => value.Position.Y);
        var nodes = target.Nodes;
        foreach (var (sourceId, nodeId) in nodeMap)
            nodes = nodes.Add(nodeId, fragment.Nodes[sourceId]);
        var replacedTargets = outputBindings
            .Select(binding => new PortReference(binding.TargetNodeId, binding.TargetPortId))
            .ToHashSet();
        var connections = target.Connections.RemoveRange(target.Connections
            .Where(pair => replacedTargets.Contains(pair.Value.Target))
            .Select(pair => pair.Key));
        foreach (var (sourceId, connectionId) in connectionMap)
        {
            var edge = fragment.Connections[sourceId];
            connections = connections.Add(connectionId, edge with
            {
                Source = edge.Source with { NodeId = nodeMap[edge.Source.NodeId] },
                Target = edge.Target with { NodeId = nodeMap[edge.Target.NodeId] }
            });
        }
        var extra = 0;
        foreach (var binding in inputBindings)
        {
            var boundary = inputMap[binding.BoundaryId];
            var id = connectionIdFactory("input." + boundary.Id + "." + extra++);
            if (!ValidId(id) || connections.ContainsKey(id) || allConnectionIds.Contains(id)) throw new ArgumentException("The connection ID factory returned a collision.", nameof(connectionIdFactory));
            connections = connections.Add(id, new(new(binding.SourceNodeId, binding.SourcePortId), new(nodeMap[boundary.TargetNodeId], boundary.TargetPortId), "data"));
        }
        foreach (var binding in outputBindings)
        {
            var boundary = outputMap[binding.BoundaryId];
            var id = connectionIdFactory("output." + boundary.Id + "." + extra++);
            if (!ValidId(id) || connections.ContainsKey(id) || allConnectionIds.Contains(id)) throw new ArgumentException("The connection ID factory returned a collision.", nameof(connectionIdFactory));
            connections = connections.Add(id, new(new(nodeMap[boundary.SourceNodeId], boundary.SourcePortId), new(binding.TargetNodeId, binding.TargetPortId), "data"));
        }
        var result = target with { Nodes = nodes, Connections = connections };
        var resultLayout = layout.Reconcile(result) with
        {
            Nodes = layout.Reconcile(result).Nodes.SetItems(nodeMap.Select(pair =>
                KeyValuePair.Create(pair.Value, new NodePresentation(new(placement.X + fragment.Layout[pair.Key].Position.X - minX,
                    placement.Y + fragment.Layout[pair.Key].Position.Y - minY))))).ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
        WorkflowValidator.ThrowIfInvalid(result);
        WorkflowLayoutJson.Validate(resultLayout, result);
        return new(result, resultLayout, nodeMap, connectionMap);
    }

    private static Func<string, string, bool, WorkflowPort?> WorkflowPortsForTarget(WorkflowDefinition target) =>
        (nodeId, portId, output) => target.Nodes.TryGetValue(nodeId, out var node)
            ? (output ? WorkflowPorts.ExplicitDataOutputs(node) : WorkflowPorts.ExplicitDataInputs(node)).SingleOrDefault(port => port.Id == portId)
            : null;

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool ValidType(string? value) => value is not null && WorkflowDataTypeCatalog.IsSupported(value);
}
