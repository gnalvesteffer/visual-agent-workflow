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
        // System.Text.Json writes environment-native indented line endings. Definitions and their hashes
        // must be byte-stable across Windows and Unix, so the portable form always uses LF.
        return JsonSerializer.Serialize(normalized, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
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

    /// <summary>Accepts the canonical hash and the prior Windows CRLF hash for persisted cross-version evidence.</summary>
    public static bool MatchesHash(WorkflowDefinition definition, string? hash)
    {
        if (hash is null) return false;
        var serialized = Serialize(definition);
        if (string.Equals(Hash(serialized), hash, StringComparison.Ordinal)) return true;

        // Before the cross-platform serialization fix, Windows hashed the same JSON with CRLF indentation.
        return string.Equals(Hash(serialized.Replace("\n", "\r\n", StringComparison.Ordinal)), hash, StringComparison.Ordinal);
    }

    private static string Hash(string serialized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));

}
