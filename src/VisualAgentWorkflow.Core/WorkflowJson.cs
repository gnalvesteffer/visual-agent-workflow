using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public static class WorkflowJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    public static string Serialize(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var normalized = definition with
        {
            Nodes = definition.Nodes.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Connections = definition.Connections.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(normalized, Options);
    }

    public static WorkflowDefinition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new ArgumentException("Definition exceeds the 1 MiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
            ?? throw new JsonException("A definition is required.");
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition;
    }

    // This is our v1 canonical representation, not a general JSON canonicalization standard.
    public static string ComputeHash(WorkflowDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(definition))));

}
