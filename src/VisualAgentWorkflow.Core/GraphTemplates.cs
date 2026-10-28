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

/// <summary>Result of expanding a template snapshot; source mapping is informational and not a live dependency.</summary>
public sealed record GraphTemplateExpansion(
    GraphFunctionDefinition Definition,
    ImmutableSortedDictionary<string, string> SourceNodeIds);

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
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(template, Options)));
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
        if (string.IsNullOrWhiteSpace(functionId))
            throw new ArgumentException("An expanded graph function ID is required.", nameof(functionId));

        nodeIdFactory ??= _ => "node-" + Guid.NewGuid().ToString("N");
        var ids = template.Body.Nodes.Keys.ToImmutableSortedDictionary(
            id => id, id => nodeIdFactory(id), StringComparer.Ordinal);
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
        return new(body, ids.ToImmutableSortedDictionary(
            pair => pair.Value, pair => pair.Key, StringComparer.Ordinal));
    }
}

/// <summary>Creates a template snapshot from an already validated pure graph function.</summary>
public static class GraphTemplateExtraction
{
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
}
