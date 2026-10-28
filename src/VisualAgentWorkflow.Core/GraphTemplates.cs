using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Authoring-only snapshot of a bounded pure data graph. It is expanded into ordinary graph nodes; it is never a runtime call.</summary>
public sealed record GraphTemplateDefinition(
    string TemplateId,
    int Version,
    string DisplayName,
    string Description,
    GraphFunctionDefinition Body);

public sealed record GraphTemplateConfiguration(GraphTemplateDefinition Definition, string ContentHash);
public sealed record GraphTemplateDocument(string FormatVersion, GraphTemplateDefinition Definition, string ContentHash);

/// <summary>Result of expanding a template snapshot; source mapping is informational and not a live dependency.</summary>
public sealed record GraphTemplateExpansion(
    GraphFunctionDefinition Definition,
    ImmutableSortedDictionary<string, string> SourceNodeIds);

/// <summary>An explicit typed input boundary from a template port to a selected workflow node input.</summary>
public sealed record GraphTemplateInputBoundary(string Id, string DisplayName, string DataType, string TargetNodeId, string TargetPortId);

/// <summary>An explicit typed output boundary from a selected workflow node output to a template port.</summary>
public sealed record GraphTemplateOutputBoundary(string Id, string DisplayName, string DataType, string SourceNodeId, string SourcePortId);

public static class GraphTemplateLimits
{
    public const string CurrentFormatVersion = "1.0";
    public const int MaximumJsonBytes = 1_048_576;
    public const int MaximumExpansionNodes = GraphFunctionLimits.MaximumNodes;
    public const int MaximumExpansionConnections = GraphFunctionLimits.MaximumConnections;
}

/// <summary>Validation and copy-on-expand proof seam for pure graph templates.</summary>
public static class GraphTemplateJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static string ComputeHash(GraphTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SerializeDefinition(template))));
    }

    /// <summary>Returns the canonical definition payload. The template hash covers these bytes, not the document envelope.</summary>
    public static string Serialize(GraphTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return SerializeDefinition(template);
    }

    public static GraphTemplateDefinition Deserialize(string json)
    {
        var template = DeserializeValue<GraphTemplateDefinition>(json, "A graph template definition is required.");
        ThrowIfInvalid(template);
        return template;
    }

    /// <summary>Serializes an integrity-checked, versioned template document.</summary>
    public static string Serialize(GraphTemplateConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfInvalid(configuration.Definition);
        var expectedHash = ComputeHash(configuration.Definition);
        if (!string.Equals(expectedHash, configuration.ContentHash, StringComparison.Ordinal))
            throw new ArgumentException("The graph template content hash does not match its definition.", nameof(configuration));
        return JsonSerializer.Serialize(new GraphTemplateDocument(GraphTemplateLimits.CurrentFormatVersion,
            configuration.Definition, expectedHash), Options);
    }

    /// <summary>Reads and validates a versioned template document, including its definition hash.</summary>
    public static GraphTemplateConfiguration DeserializeConfiguration(string json)
    {
        var document = DeserializeValue<GraphTemplateDocument>(json, "A graph template document is required.");
        if (!string.Equals(document.FormatVersion, GraphTemplateLimits.CurrentFormatVersion, StringComparison.Ordinal))
            throw new JsonException($"Unsupported graph template format version '{document.FormatVersion}'.");
        ThrowIfInvalid(document.Definition);
        var actualHash = ComputeHash(document.Definition);
        if (!string.Equals(actualHash, document.ContentHash, StringComparison.Ordinal))
            throw new JsonException("The graph template content hash does not match its definition.");
        return new(document.Definition, actualHash);
    }

    public static bool MatchesHash(GraphTemplateDefinition template, string? expectedHash) =>
        expectedHash is not null && string.Equals(ComputeHash(template), expectedHash, StringComparison.Ordinal);

    private static bool IsValidIdentifier(string? id) => id is { Length: > 0 and <= GraphFunctionLimits.MaximumIdLength } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string SerializeDefinition(GraphTemplateDefinition template)
    {
        var normalized = template with
        {
            Body = template.Body with
            {
                Inputs = template.Body.Inputs.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Outputs = template.Body.Outputs.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Nodes = template.Body.Nodes.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Connections = template.Body.Connections.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            }
        };
        return JsonSerializer.Serialize(normalized, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static T DeserializeValue<T>(string json, string missingMessage)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > GraphTemplateLimits.MaximumJsonBytes)
            throw new ArgumentException("The graph template document exceeds the 1 MiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException(missingMessage);
    }

    private static void ThrowIfInvalid(GraphTemplateDefinition? template)
    {
        var issues = Validate(template);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(template));
    }

    public static ImmutableArray<ValidationIssue> Validate(GraphTemplateDefinition? template)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));

        if (template is null)
        {
            Error("template.missing", "graphTemplate.definition", "A graph template definition is required.");
            return issues.ToImmutable();
        }

        if (string.IsNullOrWhiteSpace(template.TemplateId) || template.TemplateId.Length > GraphFunctionLimits.MaximumIdLength ||
            template.TemplateId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            Error("template.id", "graphTemplate.definition.templateId", "Template IDs must use letters, digits, '.', '_' or '-' and fit within 128 characters.");
        if (template.Version < 1)
            Error("template.version", "graphTemplate.definition.version", "A graph template version must be positive.");
        if (string.IsNullOrWhiteSpace(template.DisplayName) || template.DisplayName.Length > GraphFunctionLimits.MaximumDisplayNameLength)
            Error("template.name", "graphTemplate.definition.displayName", "A display name of 1–160 characters is required.");
        if (template.Description is null || template.Description.Length > GraphFunctionLimits.MaximumDescriptionLength)
            Error("template.description", "graphTemplate.definition.description", "Template descriptions are limited to 2,000 characters.");

        if (template.Body is null)
        {
            Error("template.body", "graphTemplate.definition.body", "A pure graph-function body is required.");
            return issues.ToImmutable();
        }

        foreach (var issue in GraphFunctionJson.Validate(template.Body))
            issues.Add(issue with { Code = "template." + issue.Code, Path = issue.Path.Replace("graphFunction", "graphTemplate", StringComparison.Ordinal) });

        if (template.Body.Nodes is null || template.Body.Connections is null || template.Body.Inputs is null || template.Body.Outputs is null)
            return issues.ToImmutable();
        if (template.Body.Nodes.Count > GraphTemplateLimits.MaximumExpansionNodes ||
            template.Body.Connections.Count > GraphTemplateLimits.MaximumExpansionConnections)
            Error("template.limit", "graphTemplate.definition.body", "Template expansion exceeds the bounded pure graph limits.");

        foreach (var (nodeId, node) in template.Body.Nodes)
        {
            if (node is null || !WorkflowNodeContractCatalog.TryGet(node.Type, out var contract))
                continue;
            if (contract.Effects != WorkflowNodeEffectFlags.None)
                Error("template.effect", $"graphTemplate.definition.body.nodes.{nodeId}",
                    $"Pure templates cannot contain {node.Type} because it declares effects: {contract.Effects}.");
            if ((contract.Authoring & WorkflowNodeAuthoringFlags.DynamicPins) != 0)
                Error("template.dynamic-pins", $"graphTemplate.definition.body.nodes.{nodeId}",
                    "Pure templates cannot contain nodes whose pins are resolved dynamically.");
        }

        return issues.ToImmutable();
    }

    public static GraphTemplateExpansion Expand(GraphTemplateDefinition template, string functionId,
        Func<string, string>? nodeIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = Validate(template);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(template));
        if (!IsValidIdentifier(functionId))
            throw new ArgumentException("An expanded graph function ID must use letters, digits, '.', '_' or '-' and fit within 128 characters.", nameof(functionId));

        nodeIdFactory ??= _ => "node-" + Guid.NewGuid().ToString("N");
        var remappedIds = template.Body.Nodes.Keys.Select(id => (Source: id, Expanded: nodeIdFactory(id))).ToArray();
        if (remappedIds.Any(pair => !IsValidIdentifier(pair.Expanded)))
            throw new ArgumentException("The node ID factory must return valid nonempty IDs within 128 characters.", nameof(nodeIdFactory));
        var ids = remappedIds.ToImmutableSortedDictionary(pair => pair.Source, pair => pair.Expanded, StringComparer.Ordinal);
        if (ids.Values.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new ArgumentException("The node ID factory must return unique IDs.", nameof(nodeIdFactory));

        var nodes = template.Body.Nodes.ToImmutableSortedDictionary(
            pair => ids[pair.Key], pair => pair.Value, StringComparer.Ordinal);
        var connections = template.Body.Connections.ToImmutableSortedDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                Source = pair.Value.Source with { NodeId = ids[pair.Value.Source.NodeId] },
                Target = pair.Value.Target with { NodeId = ids[pair.Value.Target.NodeId] }
            }, StringComparer.Ordinal);
        var body = template.Body with
        {
            FunctionId = functionId,
            Version = template.Version,
            Nodes = nodes,
            Connections = connections
        };
        var expandedIssues = GraphFunctionJson.Validate(body);
        if (expandedIssues.Length > 0)
            throw new ArgumentException(string.Join("; ", expandedIssues.Select(issue => issue.Message)), nameof(nodeIdFactory));
        return new(body, ids.ToImmutableSortedDictionary(
            pair => pair.Value, pair => pair.Key, StringComparer.Ordinal));
    }
}

/// <summary>Creates a template snapshot from an already validated pure graph function.</summary>
public static class GraphTemplateExtraction
{
    /// <summary>
    /// Extracts a bounded pure data subgraph from a workflow into a template snapshot.
    /// Boundary edges are explicit so extraction never guesses hidden inputs or outputs.
    /// </summary>
    public static GraphTemplateDefinition Extract(WorkflowDefinition source, IEnumerable<string> nodeIds,
        IEnumerable<GraphTemplateInputBoundary> inputs, IEnumerable<GraphTemplateOutputBoundary> outputs,
        string templateId, int version, string displayName, string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var selected = nodeIds.ToImmutableHashSet(StringComparer.Ordinal);
        var inputBoundaries = inputs.ToImmutableArray();
        var outputBoundaries = outputs.ToImmutableArray();
        var errors = new List<string>();
        if (selected.Count == 0)
            errors.Add("At least one data node must be selected.");
        if (selected.Any(id => !source.Nodes.ContainsKey(id)))
            errors.Add("Every selected node must exist in the source workflow.");
        if (selected.Any(id => source.Nodes.TryGetValue(id, out var node) &&
            node.Type is NodeTypes.Input or NodeTypes.Output or NodeTypes.FunctionInput or NodeTypes.FunctionOutput))
            errors.Add("Workflow entry, output, and graph-function boundary nodes cannot be extracted; use explicit template boundaries.");
        if (inputBoundaries.Select(boundary => boundary.Id).Distinct(StringComparer.Ordinal).Count() != inputBoundaries.Length ||
            outputBoundaries.Select(boundary => boundary.Id).Distinct(StringComparer.Ordinal).Count() != outputBoundaries.Length)
            errors.Add("Template input and output boundary IDs must be unique within their direction.");

        foreach (var id in selected)
        {
            var node = source.Nodes[id];
            if (!WorkflowNodeContractCatalog.TryGet(node.Type, out var contract))
            {
                errors.Add($"Selected node '{id}' has an unsupported node type.");
                continue;
            }
            if (contract.Effects != WorkflowNodeEffectFlags.None)
                errors.Add($"Selected node '{id}' declares effects and cannot be part of a pure template.");
            if ((contract.Authoring & WorkflowNodeAuthoringFlags.DynamicPins) != 0)
                errors.Add($"Selected node '{id}' has dynamically resolved pins and cannot be part of a pure template.");
            if (WorkflowPorts.Explicit(node).Any(port => port.Kind == WorkflowPortKind.Execution))
                errors.Add($"Selected node '{id}' has execution pins and cannot be part of a pure data template.");
        }

        foreach (var boundary in inputBoundaries)
        {
            if (!IsValidBoundaryId(boundary.Id) || string.IsNullOrWhiteSpace(boundary.DisplayName) ||
                !WorkflowDataTypeCatalog.IsSupported(boundary.DataType) || !selected.Contains(boundary.TargetNodeId) ||
                !source.Nodes.TryGetValue(boundary.TargetNodeId, out var target))
            {
                errors.Add($"Template input boundary '{boundary.Id}' is invalid.");
                continue;
            }
            var port = WorkflowPorts.ExplicitDataInputs(target).SingleOrDefault(candidate => candidate.Id == boundary.TargetPortId);
            if (port is null || port.DataType != boundary.DataType)
                errors.Add($"Template input boundary '{boundary.Id}' must match a selected data input port.");
        }

        foreach (var boundary in outputBoundaries)
        {
            if (!IsValidBoundaryId(boundary.Id) || string.IsNullOrWhiteSpace(boundary.DisplayName) ||
                !WorkflowDataTypeCatalog.IsSupported(boundary.DataType) || !selected.Contains(boundary.SourceNodeId) ||
                !source.Nodes.TryGetValue(boundary.SourceNodeId, out var origin))
            {
                errors.Add($"Template output boundary '{boundary.Id}' is invalid.");
                continue;
            }
            var port = WorkflowPorts.ExplicitDataOutputs(origin).SingleOrDefault(candidate => candidate.Id == boundary.SourcePortId);
            if (port is null || port.DataType != boundary.DataType)
                errors.Add($"Template output boundary '{boundary.Id}' must match a selected data output port.");
        }

        var inputTargets = inputBoundaries.Select(boundary => (boundary.TargetNodeId, boundary.TargetPortId)).ToHashSet();
        foreach (var edge in source.Connections.Values)
        {
            var sourceSelected = selected.Contains(edge.Source.NodeId);
            var targetSelected = selected.Contains(edge.Target.NodeId);
            if (!sourceSelected && !targetSelected) continue;
            if (!string.Equals(edge.Kind, "data", StringComparison.Ordinal))
            {
                errors.Add("Selected subgraphs may contain data connections only.");
                continue;
            }
            if (!sourceSelected && targetSelected && !inputTargets.Contains((edge.Target.NodeId, edge.Target.PortId)))
                errors.Add($"Incoming data edge to '{edge.Target.NodeId}.{edge.Target.PortId}' must be declared as a template input boundary.");
        }
        foreach (var edge in source.ExecutionConnections?.Values ?? [])
            if (selected.Contains(edge.Source.NodeId) || selected.Contains(edge.Target.NodeId))
                errors.Add("Selected subgraphs may not depend on execution connections.");

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors), nameof(nodeIds));

        var functionInputs = inputBoundaries.ToImmutableSortedDictionary(
            boundary => boundary.Id, boundary => new GraphFunctionPort(boundary.DisplayName, boundary.DataType), StringComparer.Ordinal);
        var functionOutputs = outputBoundaries.ToImmutableSortedDictionary(
            boundary => boundary.Id, boundary => new GraphFunctionPort(boundary.DisplayName, boundary.DataType), StringComparer.Ordinal);
        var nodes = selected.ToImmutableSortedDictionary(id => id, id => source.Nodes[id], StringComparer.Ordinal);
        foreach (var boundary in inputBoundaries)
            nodes = nodes.Add("template-input." + boundary.Id,
                new NodeDefinition(NodeTypes.FunctionInput, "1.0.0")
                {
                    FunctionPortId = boundary.Id,
                    OutputDataType = boundary.DataType
                });
        foreach (var boundary in outputBoundaries)
            nodes = nodes.Add("template-output." + boundary.Id,
                new NodeDefinition(NodeTypes.FunctionOutput, "1.0.0")
                {
                    FunctionPortId = boundary.Id,
                    InputDataType = boundary.DataType
                });

        var connections = source.Connections
            .Where(pair => selected.Contains(pair.Value.Source.NodeId) && selected.Contains(pair.Value.Target.NodeId))
            .ToImmutableSortedDictionary(StringComparer.Ordinal);
        foreach (var boundary in inputBoundaries)
            connections = connections.Add("template-input." + boundary.Id,
                new(new("template-input." + boundary.Id, "out"), new(boundary.TargetNodeId, boundary.TargetPortId), "data"));
        foreach (var boundary in outputBoundaries)
            connections = connections.Add("template-output." + boundary.Id,
                new(new(boundary.SourceNodeId, boundary.SourcePortId), new("template-output." + boundary.Id, "in"), "data"));

        var body = new GraphFunctionDefinition(templateId + ".body", version, displayName, description,
            functionInputs, functionOutputs, nodes, connections);
        return Extract(body, templateId, version, displayName, description);
    }

    public static GraphTemplateDefinition Extract(GraphFunctionDefinition source, string templateId, int version,
        string displayName, string description)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourceIssues = GraphFunctionJson.Validate(source);
        if (sourceIssues.Length > 0)
            throw new ArgumentException(string.Join("; ", sourceIssues.Select(issue => issue.Message)), nameof(source));

        var template = new GraphTemplateDefinition(templateId, version, displayName, description, source);
        var issues = GraphTemplateJson.Validate(template);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(issue => issue.Message)), nameof(source));
        return template;
    }

    private static bool IsValidBoundaryId(string? id) => id is { Length: > 0 and <= GraphFunctionLimits.MaximumIdLength } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

}
