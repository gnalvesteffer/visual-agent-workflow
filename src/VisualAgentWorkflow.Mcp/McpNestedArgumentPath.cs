using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Proof-only canonical identity for a nested MCP object property path.</summary>
/// <remarks>
/// This helper is intentionally internal. Nested paths are not admitted into workflow
/// definitions, graph pins, packages, or runtime dispatch by this proof increment.
/// </remarks>
internal static class McpNestedArgumentPath
{
    public static bool TryCreatePins(McpToolSchemaDescriptor descriptor,
        out ImmutableArray<McpNestedArgumentPinProof> pins)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        pins = [];
        if (descriptor.Root.JsonType != "object" || descriptor.Root.AllowsAdditionalProperties)
            return false;

        var nestedObjects = descriptor.Root.Properties
            .Where(field => field.JsonType == "object")
            .ToArray();
        if (nestedObjects.Length != 1) return false;

        var nestedObject = nestedObjects[0];
        if (!nestedObject.Required || nestedObject.AllowsAdditionalProperties || nestedObject.Properties.IsDefaultOrEmpty)
            return false;

        var candidates = ImmutableArray.CreateBuilder<McpNestedArgumentPinProof>(nestedObject.Properties.Length);
        foreach (var field in nestedObject.Properties)
        {
            if (field.JsonType is not ("string" or "integer" or "number" or "boolean"))
                return false;

            var path = ToPointer([nestedObject.Name, field.Name]);
            candidates.Add(new(path, ToPinId([nestedObject.Name, field.Name]), field.Name,
                field.JsonType, field.Required, field.Bounds));
        }

        var ordered = candidates.OrderBy(pin => pin.Path, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(pin => pin.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
            ordered.Select(pin => pin.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            return false;

        pins = ordered;
        return true;
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
    McpSchemaBounds Bounds);
