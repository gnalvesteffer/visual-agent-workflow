using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Proof-only canonical identity for a nested MCP object property path.</summary>
/// <remarks>
/// This helper is intentionally internal. Nested paths are not admitted into workflow
/// definitions, graph pins, packages, or runtime dispatch by this proof increment.
/// </remarks>
internal static class McpNestedArgumentPath
{
    private const int MaximumObjectPathDepth = 2;
    private const string InvalidArgumentsDiagnostic =
        "Nested MCP argument values do not match the pinned closed-object contract.";

    public static bool TryCreatePins(McpToolSchemaDescriptor descriptor,
        out ImmutableArray<McpNestedArgumentPinProof> pins)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        pins = [];
        if (descriptor.Root.JsonType != "object" || descriptor.Root.AllowsAdditionalProperties)
            return false;

        var candidateObjects = new List<(McpSchemaFieldDescriptor Field, ImmutableArray<string> Path)>();
        FindCandidateObjects(descriptor.Root, [], candidateObjects);
        if (candidateObjects.Count != 1) return false;

        var (nestedObject, objectPath) = candidateObjects[0];
        var scalarFields = nestedObject.Properties.Where(property =>
            property.JsonType is "string" or "integer" or "number" or "boolean").ToArray();
        var candidates = ImmutableArray.CreateBuilder<McpNestedArgumentPinProof>(scalarFields.Length);
        foreach (var field in scalarFields)
        {
            var segments = objectPath.Add(field.Name);
            candidates.Add(new(ToPointer(segments), ToPinId(segments), field.Name,
                field.JsonType, field.Required, field.Bounds, segments));
        }

        var ordered = candidates.OrderBy(pin => pin.Path, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(pin => pin.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
            ordered.Select(pin => pin.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
            ordered.Any(pin => descriptor.Root.Properties.Any(property =>
                string.Equals($"arg.{property.Name}", pin.Id, StringComparison.Ordinal))))
            return false;

        pins = ordered;
        return true;
    }

    private static void FindCandidateObjects(McpSchemaFieldDescriptor parent,
        ImmutableArray<string> parentPath, List<(McpSchemaFieldDescriptor Field, ImmutableArray<string> Path)> candidates)
    {
        foreach (var field in parent.Properties.Where(property => property.JsonType == "object"))
        {
            if (!field.Required || field.AllowsAdditionalProperties) continue;
            var path = parentPath.Add(field.Name);
            if (path.Length > MaximumObjectPathDepth) continue;

            if (!field.Properties.IsDefaultOrEmpty && field.Properties.Any(property =>
                    property.JsonType is "string" or "integer" or "number" or "boolean"))
            {
                candidates.Add((field, path));
                continue;
            }

            if (path.Length < MaximumObjectPathDepth)
                FindCandidateObjects(field, path, candidates);
        }
    }

    public static bool TryReconstructArguments(JsonElement schema, JsonElement staticArguments,
        IReadOnlyDictionary<string, JsonElement> connectedValues, out JsonElement arguments, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(connectedValues);
        arguments = default;
        diagnostic = InvalidArgumentsDiagnostic;
        try
        {
            var descriptor = McpToolInputSchemas.Describe(schema);
            if (!TryCreatePins(descriptor, out var pins) || staticArguments.ValueKind != JsonValueKind.Object)
                return false;

            var reconstructed = JsonNode.Parse(staticArguments.GetRawText())!.AsObject();
            foreach (var (pinId, value) in connectedValues)
            {
                var pin = pins.SingleOrDefault(candidate => candidate.Id == pinId);
                if (pin is null) return false;

                var nested = reconstructed;
                foreach (var segment in pin.PathSegments.Take(pin.PathSegments.Length - 1))
                {
                    if (nested.TryGetPropertyValue(segment, out var existing))
                    {
                        if (existing is not JsonObject existingObject) return false;
                        nested = existingObject;
                    }
                    else
                    {
                        var created = new JsonObject();
                        nested[segment] = created;
                        nested = created;
                    }
                }

                nested[pin.Name] = JsonNode.Parse(value.GetRawText());
            }

            using var candidateDocument = JsonDocument.Parse(reconstructed.ToJsonString());
            _ = McpToolInputSchemas.ToCallArguments(schema, candidateDocument.RootElement);
            arguments = candidateDocument.RootElement.Clone();
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ToolInvocationException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static string ToPointer(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || segments.Any(segment => segment is null))
            throw new ArgumentException("A nested MCP path must contain at least one property segment.", nameof(segments));

        return string.Concat(segments.Select(segment => "/" + Escape(segment)));
    }

    public static string ToPinId(IReadOnlyList<string> segments)
    {
        var pointer = ToPointer(segments);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pointer)));
        return $"arg.nested.{hash}";
    }

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>Non-persisted candidate pin emitted only by the nested MCP proof.</summary>
internal sealed record McpNestedArgumentPinProof(
    string Path,
    string Id,
    string Name,
    string DataType,
    bool Required,
    McpSchemaBounds Bounds,
    ImmutableArray<string> PathSegments);
