using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Immutable evidence that a valid revision was promoted from a saveable invalid draft.</summary>
public sealed record WorkflowDraftPromotionProvenance(
    string Marker,
    string SourceWorkflowId,
    long SourceGeneration,
    string SourceDefinitionHash,
    string SourceLayoutHash,
    string SourceDefinitionJson,
    string SourceLayoutJson,
    ImmutableArray<ValidationIssue> SourceDiagnostics,
    Guid TargetRevisionId,
    string TargetSchema,
    string TargetContentHash)
{
    public const string CurrentMarker = "sequence-workflow-call-draft-v1";

    public static string Serialize(WorkflowDraftPromotionProvenance provenance) =>
        JsonSerializer.Serialize(provenance, JsonOptions);

    public static WorkflowDraftPromotionProvenance Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<WorkflowDraftPromotionProvenance>(json, JsonOptions)
            ?? throw new JsonException("Draft promotion provenance is required.");
    }

    public static string ComputeHash(WorkflowDraftPromotionProvenance provenance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(provenance)))).ToLowerInvariant();

    public void Validate(WorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Marker != CurrentMarker || SourceWorkflowId != target.Definition.WorkflowId ||
            SourceGeneration < 0 || TargetRevisionId != target.RevisionId ||
            TargetSchema != target.Definition.SchemaVersion || TargetContentHash != target.ContentHash ||
            !IsSha256(SourceDefinitionHash) || !IsSha256(SourceLayoutHash) ||
            string.IsNullOrWhiteSpace(SourceDefinitionJson) || string.IsNullOrWhiteSpace(SourceLayoutJson) ||
            SourceDiagnostics.IsDefaultOrEmpty || SourceDiagnostics.Length > 256 ||
            SourceDiagnostics.Any(issue => issue is null || string.IsNullOrWhiteSpace(issue.Code) ||
                string.IsNullOrWhiteSpace(issue.Path) || string.IsNullOrWhiteSpace(issue.Message)))
            throw new InvalidDataException("Draft promotion provenance does not match its target revision.");
        if (target.Definition.SchemaVersion != WorkflowSchemaVersions.SequenceWorkflowCallGraph ||
            !WorkflowValidator.Validate(target.Definition).IsEmpty)
            throw new InvalidDataException("Draft promotion provenance requires a valid schema 1.50 target.");
        var source = WorkflowJson.DeserializeDraft(SourceDefinitionJson);
        var layout = WorkflowLayoutJson.Deserialize(SourceLayoutJson, source);
        if (source.WorkflowId != SourceWorkflowId || WorkflowJson.ComputeDraftHash(source) != SourceDefinitionHash ||
            WorkflowLayoutJson.ComputeHash(layout, source) != SourceLayoutHash ||
            !string.Equals(WorkflowJson.SerializeDraft(source), SourceDefinitionJson, StringComparison.Ordinal) ||
            !string.Equals(WorkflowLayoutJson.Serialize(layout, source), SourceLayoutJson, StringComparison.Ordinal))
            throw new InvalidDataException("Draft promotion source envelope is not canonical.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 32
    };
}

/// <summary>A non-revision draft envelope submitted to the explicit WorkflowCall promotion boundary.</summary>
public sealed record WorkflowDraftPromotionCandidate(
    WorkflowDefinition Definition,
    WorkflowLayout Layout,
    long Generation,
    string DefinitionHash,
    string LayoutHash,
    ImmutableArray<ValidationIssue> Diagnostics,
    string Marker = WorkflowDraftPromotionProvenance.CurrentMarker)
{
    public static WorkflowDraftPromotionCandidate Create(WorkflowDefinition definition, WorkflowLayout layout,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(layout);
        var diagnostics = WorkflowValidator.Validate(definition);
        if (diagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("A draft promotion candidate must preserve the source draft diagnostics.", nameof(definition));
        var normalized = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(definition));
        var normalizedLayout = WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(layout, normalized), normalized);
        return new(normalized, normalizedLayout, generation, WorkflowJson.ComputeDraftHash(normalized),
            WorkflowLayoutJson.ComputeHash(normalizedLayout, normalized), diagnostics);
    }
}

/// <summary>Result of explicitly promoting a saveable draft; source state never becomes a revision.</summary>
public sealed record WorkflowDraftPromotionResult(
    WorkflowDraftPromotionCandidate SourceCandidate,
    WorkflowRevision TargetRevision,
    WorkflowDraftPromotionProvenance Provenance);

/// <summary>Explicit, narrow draft-to-schema-1.50 promotion for call-bearing Sequence graphs.</summary>
public static class WorkflowDraftPromotion
{
    public static WorkflowDraftPromotionResult TryPromote(WorkflowDraftPromotionCandidate candidate, string author,
        Guid? parentRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("An author is required.", nameof(author));
        if (candidate.Marker != WorkflowDraftPromotionProvenance.CurrentMarker)
            throw new ArgumentException("The draft promotion marker is unsupported.", nameof(candidate));
        if (candidate.Definition.SchemaVersion != WorkflowSchemaVersions.SequenceTransformChainGraph)
            throw new ArgumentException("Only schema 1.49 drafts may be promoted by this operation.", nameof(candidate));
        if (candidate.DefinitionHash != WorkflowJson.ComputeDraftHash(candidate.Definition) ||
            candidate.LayoutHash != WorkflowLayoutJson.ComputeHash(candidate.Layout, candidate.Definition))
            throw new ArgumentException("The draft promotion candidate hashes do not match its canonical source.", nameof(candidate));
        var sourceDiagnostics = WorkflowValidator.Validate(candidate.Definition);
        if (sourceDiagnostics.IsDefaultOrEmpty || !sourceDiagnostics.SequenceEqual(candidate.Diagnostics))
            throw new ArgumentException("The draft diagnostics no longer match its canonical source definition.", nameof(candidate));

        var targetDefinition = candidate.Definition with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallGraph };
        WorkflowValidator.ThrowIfInvalid(targetDefinition);
        var target = WorkflowRevision.Create(targetDefinition, author, parentRevisionId) with { Layout = candidate.Layout };
        var provenance = new WorkflowDraftPromotionProvenance(
            candidate.Marker, candidate.Definition.WorkflowId, candidate.Generation,
            candidate.DefinitionHash, candidate.LayoutHash,
            WorkflowJson.SerializeDraft(candidate.Definition),
            WorkflowLayoutJson.Serialize(candidate.Layout, candidate.Definition), candidate.Diagnostics,
            target.RevisionId, target.Definition.SchemaVersion, target.ContentHash);
        provenance.Validate(target);
        return new(candidate, target with { DraftPromotionProvenance = provenance }, provenance);
    }
}
