using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>One explicitly named, forward-only migration operation.</summary>
public sealed record WorkflowMigrationDescriptor(
    string OperationId,
    string FromSchema,
    string ToSchema,
    string ImplementationVersion);

/// <summary>Input to a non-persisting, source-preserving migration attempt.</summary>
public sealed record WorkflowMigrationRequest(
    WorkflowRevision SourceRevision,
    ImmutableArray<string> OperationIds,
    string Author);

/// <summary>Immutable provenance for a successful migration candidate.</summary>
public sealed record WorkflowMigrationProvenance(
    Guid SourceRevisionId,
    string SourceSchema,
    string SourceContentHash,
    Guid TargetRevisionId,
    string TargetSchema,
    string TargetContentHash,
    ImmutableArray<string> OperationIds,
    ImmutableArray<string> ImplementationVersions);

/// <summary>Structured migration failure; no target revision is produced on failure.</summary>
public sealed record WorkflowMigrationDiagnostic(string Code, string Message, string? OperationId = null);

/// <summary>Result of a dry-run migration attempt. Persistence remains the host's responsibility.</summary>
public sealed record WorkflowMigrationAttempt(
    WorkflowMigrationResult? Result,
    ImmutableArray<WorkflowMigrationDiagnostic> Diagnostics)
{
    public bool Succeeded => Result is not null && Diagnostics.IsDefaultOrEmpty;
}

/// <summary>Source-preserving migrated revision candidate and its deterministic provenance.</summary>
public sealed record WorkflowMigrationResult(
    WorkflowRevision SourceRevision,
    WorkflowRevision TargetRevision,
    WorkflowMigrationProvenance Provenance);

/// <summary>
/// Explicit executable migration registry for reviewed public promotions. It is deliberately
/// separate from the discoverability-only promotion catalog and cannot admit schema 1.50.
/// </summary>
public static class WorkflowMigrationRegistry
{
    private const string ImplementationVersion = "1";

    private static readonly ImmutableDictionary<string, WorkflowMigrationDescriptor> descriptors =
        new[]
        {
            new WorkflowMigrationDescriptor("sequence-reverse-paths", "1.47", "1.48", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-transform-chains-from-uppercase", "1.47", "1.49", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-transform-chains-from-reverse", "1.48", "1.49", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-paths", "1.49", "1.50", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-transforms", "1.50", "1.51", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-json", "1.52", "1.53", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-json-chains", "1.53", "1.54", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-json-merge", "1.54", "1.55", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-json-arrays", "1.55", "1.56", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-json-control", "1.56", "1.57", ImplementationVersion),
            new WorkflowMigrationDescriptor("sequence-workflow-call-agents", "1.51", "1.52", ImplementationVersion)
        }.ToImmutableDictionary(descriptor => descriptor.OperationId, StringComparer.Ordinal);

    public static ImmutableArray<WorkflowMigrationDescriptor> All { get; } =
        descriptors.Values.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal).ToImmutableArray();

    public static WorkflowMigrationAttempt TryMigrate(WorkflowMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<WorkflowMigrationDiagnostic>();
        if (request.SourceRevision is null)
        {
            diagnostics.Add(new("source.required", "A source revision is required."));
            return new(null, diagnostics.ToImmutable());
        }
        if (string.IsNullOrWhiteSpace(request.Author))
            diagnostics.Add(new("author.required", "A migration author is required."));
        if (request.OperationIds.IsDefaultOrEmpty)
            diagnostics.Add(new("operations.required", "At least one explicit migration operation is required."));
        if (!WorkflowJson.MatchesHash(request.SourceRevision.Definition, request.SourceRevision.ContentHash))
            diagnostics.Add(new("source.hash", "The source revision content does not match its stored hash."));

        if (diagnostics.Count > 0) return new(null, diagnostics.ToImmutable());

        var source = request.SourceRevision;
        var current = source.Definition;
        var applied = ImmutableArray.CreateBuilder<string>();
        var versions = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operationId in request.OperationIds)
        {
            if (!descriptors.TryGetValue(operationId, out var descriptor))
            {
                diagnostics.Add(new("operation.unknown", $"Migration operation '{operationId}' is not registered.", operationId));
                break;
            }
            if (!seen.Add(operationId))
            {
                diagnostics.Add(new("operation.duplicate", $"Migration operation '{operationId}' was requested more than once.", operationId));
                break;
            }
            if (current.SchemaVersion != descriptor.FromSchema)
            {
                diagnostics.Add(new("operation.source-schema",
                    $"Migration operation '{operationId}' requires schema {descriptor.FromSchema}, but the current definition is {current.SchemaVersion}.", operationId));
                break;
            }

            try
            {
                current = Apply(operationId, current);
                WorkflowValidator.ThrowIfInvalid(current);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                diagnostics.Add(new("operation.rejected", exception.Message, operationId));
                break;
            }
            if (current.SchemaVersion != descriptor.ToSchema)
            {
                diagnostics.Add(new("operation.target-schema",
                    $"Migration operation '{operationId}' produced schema {current.SchemaVersion}, expected {descriptor.ToSchema}.", operationId));
                break;
            }
            applied.Add(operationId);
            versions.Add(descriptor.ImplementationVersion);
        }

        if (diagnostics.Count > 0) return new(null, diagnostics.ToImmutable());
        if (current == source.Definition)
        {
            diagnostics.Add(new("target.unchanged", "The migration did not produce a new definition."));
            return new(null, diagnostics.ToImmutable());
        }

        WorkflowRevision target;
        try
        {
            target = WorkflowRevision.Create(current, request.Author, source.RevisionId) with
            {
                Layout = source.Layout,
                SourceProvenance = source.SourceProvenance
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new("target.invalid", exception.Message));
            return new(null, diagnostics.ToImmutable());
        }

        var provenance = new WorkflowMigrationProvenance(source.RevisionId, source.Definition.SchemaVersion,
            source.ContentHash, target.RevisionId, target.Definition.SchemaVersion, target.ContentHash,
            applied.ToImmutable(), versions.ToImmutable());
        target = target with { MigrationProvenance = provenance };
        ValidateProvenance(provenance, target);
        return new(new WorkflowMigrationResult(source, target, provenance), diagnostics.ToImmutable());
    }

    public static void ValidateProvenance(WorkflowMigrationProvenance provenance, WorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(target);
        if (provenance.SourceRevisionId == Guid.Empty || provenance.TargetRevisionId == Guid.Empty ||
            provenance.TargetRevisionId != target.RevisionId || target.ParentRevisionId != provenance.SourceRevisionId ||
            string.IsNullOrWhiteSpace(provenance.SourceSchema) ||
            provenance.TargetSchema != target.Definition.SchemaVersion || provenance.TargetContentHash != target.ContentHash ||
            provenance.OperationIds.IsDefaultOrEmpty || provenance.OperationIds.Length > 16 ||
            provenance.OperationIds.Length != provenance.ImplementationVersions.Length ||
            provenance.OperationIds.Any(string.IsNullOrWhiteSpace) ||
            provenance.ImplementationVersions.Any(string.IsNullOrWhiteSpace) ||
            !IsSha256(provenance.SourceContentHash) || !IsSha256(provenance.TargetContentHash))
            throw new InvalidDataException("Migration provenance does not match its target revision.");

        var currentSchema = provenance.SourceSchema;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operationId in provenance.OperationIds)
        {
            if (!descriptors.TryGetValue(operationId, out var descriptor) || !seen.Add(operationId) ||
                descriptor.FromSchema != currentSchema)
                throw new InvalidDataException("Migration provenance contains an unknown, duplicate, or non-contiguous operation.");
            currentSchema = descriptor.ToSchema;
        }
        if (currentSchema != provenance.TargetSchema)
            throw new InvalidDataException("Migration provenance has an unsupported target schema.");
    }

    public static string SerializeProvenance(WorkflowMigrationProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return JsonSerializer.Serialize(provenance, ProvenanceJsonOptions);
    }

    public static string ComputeProvenanceHash(WorkflowMigrationProvenance provenance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeProvenance(provenance)))).ToLowerInvariant();

    public static WorkflowMigrationProvenance DeserializeProvenance(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<WorkflowMigrationProvenance>(json, ProvenanceJsonOptions)
            ?? throw new JsonException("Migration provenance is required.");
    }

    private static readonly JsonSerializerOptions ProvenanceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16
    };

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static WorkflowDefinition Apply(string operationId, WorkflowDefinition definition) => operationId switch
    {
        "sequence-reverse-paths" => GraphEditing.EnableSequenceReversePathGraph(definition),
        "sequence-transform-chains-from-uppercase" => GraphEditing.EnableSequenceTransformChainGraph(definition),
        "sequence-transform-chains-from-reverse" => GraphEditing.EnableSequenceTransformChainGraph(definition),
        "sequence-workflow-call-paths" => GraphEditing.EnableSequenceWorkflowCallGraph(definition),
        "sequence-workflow-call-transforms" => GraphEditing.EnableSequenceWorkflowCallTransformGraph(definition),
        "sequence-workflow-call-agents" => GraphEditing.EnableSequenceWorkflowCallAgentGraph(definition),
        "sequence-workflow-call-json" => GraphEditing.EnableSequenceWorkflowCallJsonGraph(definition),
        "sequence-workflow-call-json-chains" => GraphEditing.EnableSequenceWorkflowCallJsonChainGraph(definition),
        "sequence-workflow-call-json-merge" => GraphEditing.EnableSequenceWorkflowCallJsonMergeGraph(definition),
        "sequence-workflow-call-json-arrays" => GraphEditing.EnableSequenceWorkflowCallJsonArrayGraph(definition),
        "sequence-workflow-call-json-control" => GraphEditing.EnableSequenceWorkflowCallJsonControlGraph(definition),
        _ => throw new InvalidOperationException($"Migration operation '{operationId}' is not executable.")
    };
}
