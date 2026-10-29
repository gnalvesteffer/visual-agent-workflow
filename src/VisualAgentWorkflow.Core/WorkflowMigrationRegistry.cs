using System.Collections.Immutable;

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
            new WorkflowMigrationDescriptor("sequence-transform-chains-from-reverse", "1.48", "1.49", ImplementationVersion)
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
        return new(new WorkflowMigrationResult(source, target, provenance), diagnostics.ToImmutable());
    }

    private static WorkflowDefinition Apply(string operationId, WorkflowDefinition definition) => operationId switch
    {
        "sequence-reverse-paths" => GraphEditing.EnableSequenceReversePathGraph(definition),
        "sequence-transform-chains-from-uppercase" => GraphEditing.EnableSequenceTransformChainGraph(definition),
        "sequence-transform-chains-from-reverse" => GraphEditing.EnableSequenceTransformChainGraph(definition),
        _ => throw new InvalidOperationException($"Migration operation '{operationId}' is not executable.")
    };
}
