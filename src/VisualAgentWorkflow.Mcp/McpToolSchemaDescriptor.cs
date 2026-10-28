using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Derived, bounded metadata for one validated MCP input schema.</summary>
/// <remarks>
/// This is an authoring/introspection view. It is not persisted in workflow definitions, does not
/// grant tool authority, and does not replace broker validation. Nested fields are described but are
/// not implicitly flattened into graph pins.
/// </remarks>
public sealed record McpToolSchemaDescriptor(
    string DescriptorVersion,
    string NormalizedSchemaHash,
    McpSchemaFieldDescriptor Root);

/// <summary>One stable schema field or nested property.</summary>
public sealed record McpSchemaFieldDescriptor(
    string Path,
    string Name,
    string JsonType,
    bool Required,
    bool DirectGraphPinEligible,
    string? Description,
    string? DefaultJson,
    ImmutableArray<string> EnumValues,
    McpSchemaBounds Bounds,
    ImmutableArray<McpSchemaFieldDescriptor> Properties,
    McpSchemaFieldDescriptor? Items,
    bool AllowsAdditionalProperties);

/// <summary>Supported scalar and collection bounds declared by a schema field.</summary>
public sealed record McpSchemaBounds(
    int? MinimumLength = null,
    int? MaximumLength = null,
    int? MinimumItems = null,
    int? MaximumItems = null,
    double? Minimum = null,
    double? Maximum = null);

internal static class McpToolSchemaDescriptorBuilder
{
    private const int MaximumDescriptionLength = 2_048;
    private const int MaximumDefaultLength = 4_096;

    public static McpToolSchemaDescriptor Build(JsonElement schema, JsonElement normalized, string version)
    {
        var normalizedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized.GetRawText())));
        return new(version, normalizedHash, Field(schema, "$", "", required: true, topLevel: false));
    }

    private static McpSchemaFieldDescriptor Field(JsonElement definition, string path, string name,
        bool required, bool topLevel)
    {
        var any = definition.ValueKind == JsonValueKind.Object && !definition.EnumerateObject().Any();
        var type = any ? "json" : definition.GetProperty("type").GetString()!;
        var properties = ImmutableArray<McpSchemaFieldDescriptor>.Empty;
        var allowsAdditionalProperties = false;
        if (type == "object")
        {
            var requiredNames = RequiredNames(definition);
            if (definition.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object)
            {
                properties = [.. declared.EnumerateObject().Select(property => Field(property.Value,
                    Join(path, property.Name), property.Name, requiredNames.Contains(property.Name), topLevel: path == "$"))];
            }
            allowsAdditionalProperties = definition.TryGetProperty("additionalProperties", out var additional) &&
                (additional.ValueKind == JsonValueKind.Object || additional.ValueKind == JsonValueKind.True);
        }

        McpSchemaFieldDescriptor? items = null;
        if (type == "array" && definition.TryGetProperty("items", out var itemSchema))
            items = Field(itemSchema, Join(path, "[]"), "[]", required: true, topLevel: false);

        return new(path, name, type, required,
            topLevel && type is "string" or "integer" or "number" or "boolean",
            BoundedDescription(definition), BoundedDefault(definition), EnumValues(definition), Bounds(definition),
            properties, items, allowsAdditionalProperties);
    }

    private static HashSet<string> RequiredNames(JsonElement definition) =>
        definition.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);

    private static string Join(string parent, string child) => parent == "$" ? $"$.{child}" : $"{parent}.{child}";

    private static string? BoundedDescription(JsonElement definition)
    {
        if (!definition.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
            return null;
        var value = description.GetString()?.Trim();
        return value is null || value.Length > MaximumDescriptionLength ||
            value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            ? null : value;
    }

    private static string? BoundedDefault(JsonElement definition)
    {
        if (!definition.TryGetProperty("default", out var value)) return null;
        var raw = value.GetRawText();
        return raw.Length <= MaximumDefaultLength ? raw : null;
    }

    private static ImmutableArray<string> EnumValues(JsonElement definition) =>
        definition.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray().Select(value => value.GetRawText())]
            : [];

    private static McpSchemaBounds Bounds(JsonElement definition) => new(
        Integer(definition, "minLength"), Integer(definition, "maxLength"),
        Integer(definition, "minItems"), Integer(definition, "maxItems"),
        Number(definition, "minimum"), Number(definition, "maximum"));

    private static int? Integer(JsonElement definition, string name) =>
        definition.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static double? Number(JsonElement definition, string name) =>
        definition.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
}
