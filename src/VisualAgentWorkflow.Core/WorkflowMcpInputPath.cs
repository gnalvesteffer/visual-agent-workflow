using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VisualAgentWorkflow.Core;

internal static class WorkflowMcpInputPath
{
    private const int MaximumPathSegments = 3;

    public static string NestedPinId(string jsonPointer)
    {
        ArgumentNullException.ThrowIfNull(jsonPointer);
        return $"arg.nested.{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jsonPointer)))}";
    }

    public static string DisplayName(McpInputPortDefinition argument) =>
        TryGetSegments(argument, out var segments) ? string.Join(" › ", segments) : argument.Name;

    public static bool TryGetSegments(McpInputPortDefinition argument, out ImmutableArray<string> segments)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.JsonPointer is null)
        {
            segments = [argument.Name];
            return !string.IsNullOrWhiteSpace(argument.Name) && argument.Id == $"arg.{argument.Name}";
        }

        segments = [];
        var pointer = argument.JsonPointer;
        if (!pointer.StartsWith("/", StringComparison.Ordinal)) return false;
        var rawSegments = pointer[1..].Split('/');
        if (rawSegments.Length is < 2 or > MaximumPathSegments) return false;

        var builder = ImmutableArray.CreateBuilder<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            if (!TryUnescape(raw, out var segment) || string.IsNullOrWhiteSpace(segment)) return false;
            builder.Add(segment);
        }

        segments = builder.ToImmutable();
        return segments[^1] == argument.Name && argument.Id == NestedPinId(pointer) && ToJsonPointer(segments) == pointer;
    }

    public static bool IsOverlappingPath(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        var sharedLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < sharedLength; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    public static bool ContainsValue(JsonNode root, ImmutableArray<string> segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current)) return false;
        }
        return true;
    }

    public static bool TrySetValue(JsonObject root, ImmutableArray<string> segments, JsonNode value)
    {
        var current = root;
        foreach (var segment in segments.Take(segments.Length - 1))
        {
            if (current.TryGetPropertyValue(segment, out var existing))
            {
                if (existing is not JsonObject nested) return false;
                current = nested;
            }
            else
            {
                var nested = new JsonObject();
                current[segment] = nested;
                current = nested;
            }
        }

        current[segments[^1]] = value;
        return true;
    }

    private static string ToJsonPointer(IEnumerable<string> segments) =>
        string.Concat(segments.Select(segment => "/" + segment.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal)));

    private static bool TryUnescape(string value, out string segment)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '~')
            {
                result.Append(value[index]);
                continue;
            }
            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                segment = string.Empty;
                return false;
            }
            result.Append(value[index] == '0' ? '~' : '/');
        }
        segment = result.ToString();
        return true;
    }
}
