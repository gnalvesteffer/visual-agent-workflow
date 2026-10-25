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
        return SerializeNormalized(definition);
    }

    /// <summary>Canonical representation for editor working state; never use this to publish or execute.</summary>
    public static string SerializeDraft(WorkflowDefinition definition)
    {
        ValidateDraft(definition);
        return SerializeNormalized(definition);
    }

    /// <summary>Reads editable working state while accepting incomplete graph topology. Never use for publish or execution.</summary>
    public static WorkflowDefinition DeserializeDraft(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576)
            throw new ArgumentException("Definition exceeds the 1 MiB import limit.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
            ?? throw new JsonException("A definition is required.");
        ValidateDraft(definition);
        return definition;
    }

    private static void ValidateDraft(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = WorkflowValidator.Validate(definition);
        var draftableTopologyIssues = new HashSet<string>(StringComparer.Ordinal)
        {
            "pipeline.degree", "graph.degree", "graph.unreachable", "graph.cycle"
        };
        var blockingIssue = issues.FirstOrDefault(issue => !draftableTopologyIssues.Contains(issue.Code));
        if (blockingIssue is not null)
            throw new ArgumentException($"Draft structure is invalid at {blockingIssue.Path}: {blockingIssue.Message}", nameof(definition));
    }

    private static string SerializeNormalized(WorkflowDefinition definition)
    {
        var normalized = definition with
        {
            Nodes = definition.Nodes.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Connections = definition.Connections.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            ExecutionConnections = definition.ExecutionConnections?.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
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

    /// <summary>Draft-state hash for editor change tracking; it is not a content identity or publishable hash.</summary>
    public static string ComputeDraftHash(WorkflowDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeDraft(definition))));

    /// <summary>Checks content identity independently of current validation rules, including archived run evidence.</summary>
    public static bool MatchesHash(WorkflowDefinition definition, string? hash)
    {
        if (hash is null) return false;
        // A stored run is historical evidence. A later validator may reject an old definition even though
        // the bytes and captured hash are intact. Callers that accept executable revisions validate separately.
        var serialized = SerializeNormalized(definition);
        if (string.Equals(Hash(serialized), hash, StringComparison.Ordinal)) return true;

        // Before the cross-platform serialization fix, Windows hashed the same JSON with CRLF indentation.
        return string.Equals(Hash(serialized.Replace("\n", "\r\n", StringComparison.Ordinal)), hash, StringComparison.Ordinal);
    }

    private static string Hash(string serialized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));

}
