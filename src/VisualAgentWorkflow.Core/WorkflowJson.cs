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
        // Schema 1.33 collaboration regions are configured in stages: authors add the node first,
        // then define its branch pins, region channels, timeout, grants, and finally wire the routes.
        // Keep every intermediate state editable and persistable as a draft. Strict serialization,
        // publication, and execution still use WorkflowValidator.ThrowIfInvalid.
        // Schema 1.34 branch-local routes are also authored incrementally; preserve incomplete branch
        // outcomes for draft recovery while strict serialization, publication, and execution stay strict.
        var blockingIssue = issues.FirstOrDefault(issue => !IsDraftableIssue(definition, issue));
        if (blockingIssue is not null)
            throw new ArgumentException($"Draft structure is invalid at {blockingIssue.Path}: {blockingIssue.Message}", nameof(definition));
    }

    internal static bool HasOnlyDraftableIssues(WorkflowDefinition definition)
    {
        var issues = WorkflowValidator.Validate(definition);
        return !issues.IsEmpty && issues.All(issue => IsDraftableIssue(definition, issue));
    }

    private static bool IsDraftableIssue(WorkflowDefinition definition, ValidationIssue issue) =>
        IsDraftableIssue(issue) || (definition.SchemaVersion is WorkflowSchemaVersions.SequenceTransformChainGraph or WorkflowSchemaVersions.SequenceWorkflowCallGraph or WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph or WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph) &&
        definition.Nodes.Values.Any(node => node.Type == NodeTypes.WorkflowCall) &&
        (issue.Code == "execution.node.unsupported" || issue.Code.StartsWith("workflowCall.", StringComparison.Ordinal) ||
            issue.Code.StartsWith("sequence.call.", StringComparison.Ordinal));

    private static bool IsDraftableIssue(ValidationIssue issue) =>
        issue.Code is "workflow.degree" or "graph.degree" or "graph.unreachable" or "graph.cycle" or "data.unreachable" or
            "data.degree" or "data.unused" or "execution.port" or "execution.target" or "execution.degree" or
            "execution.pure" or "execution.path" or "parallel.rendezvous.configuration" or "parallel.block.count" ||
        issue.Code.StartsWith("parallel.branch.", StringComparison.Ordinal) ||
        issue.Code.StartsWith("parallel.join.", StringComparison.Ordinal) ||
        issue.Code.StartsWith("parallel.fork.", StringComparison.Ordinal) ||
        issue.Code.StartsWith("parallel.anchor.", StringComparison.Ordinal) ||
        issue.Code.StartsWith("parallel.collaboration.", StringComparison.Ordinal);

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
