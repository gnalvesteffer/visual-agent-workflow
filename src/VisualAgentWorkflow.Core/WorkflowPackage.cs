using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public sealed record PackageRequirement(string Kind, string Id, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContractHash = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DescriptionHash { get; init; }
}
public sealed record WorkflowPackageMigrationProvenance(
    WorkflowMigrationProvenance Revision,
    string SourcePackageHash,
    string SourcePackageJson);
public sealed record WorkflowPackageDraftPromotionProvenance(
    WorkflowDraftPromotionProvenance Draft,
    string SourceDefinitionJson,
    string SourceLayoutJson);
public sealed record WorkflowPackage(string FormatVersion, WorkflowDefinition Definition, WorkflowLayout Layout,
    ImmutableArray<PackageRequirement> Requirements, string DefinitionHash, string LayoutHash,
    WorkflowPackageMigrationProvenance? MigrationProvenance = null,
    WorkflowPackageDraftPromotionProvenance? DraftPromotionProvenance = null);

/// <summary>Self-contained design sharing. Checksums are integrity checks, never signatures or permissions.</summary>
public static class WorkflowPackageJson
{
    public const int MaximumBytes = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true, MaxDepth = 64
    };

    public static WorkflowPackage Create(WorkflowDefinition definition, WorkflowLayout layout)
        => Create(definition, layout, null);

    public static WorkflowPackage Create(WorkflowMigrationResult migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var sourceLayout = migration.SourceRevision.Layout ?? WorkflowLayout.Create(migration.SourceRevision.Definition);
        var sourcePackageJson = Serialize(migration.SourceRevision.Definition, sourceLayout);
        var provenance = new WorkflowPackageMigrationProvenance(migration.Provenance,
            ComputePackageHash(sourcePackageJson), sourcePackageJson);
        return Create(migration.TargetRevision.Definition,
            migration.TargetRevision.Layout ?? WorkflowLayout.Create(migration.TargetRevision.Definition), provenance);
    }

    public static WorkflowPackage Create(WorkflowDraftPromotionResult promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var sourceDefinitionJson = WorkflowJson.SerializeDraft(promotion.SourceCandidate.Definition);
        var sourceLayoutJson = WorkflowLayoutJson.Serialize(promotion.SourceCandidate.Layout, promotion.SourceCandidate.Definition);
        var provenance = new WorkflowPackageDraftPromotionProvenance(promotion.Provenance, sourceDefinitionJson, sourceLayoutJson);
        return Create(promotion.TargetRevision.Definition,
            promotion.TargetRevision.Layout ?? WorkflowLayout.Create(promotion.TargetRevision.Definition), null, provenance);
    }

    public static WorkflowPackage Create(WorkflowRevision revision, WorkflowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(layout);
        if (revision.DraftPromotionProvenance is not { } provenance)
            return Create(revision.Definition, layout);
        var packageProvenance = new WorkflowPackageDraftPromotionProvenance(provenance,
            provenance.SourceDefinitionJson, provenance.SourceLayoutJson);
        return Create(revision.Definition, layout, null, packageProvenance);
    }

    private static WorkflowPackage Create(WorkflowDefinition definition, WorkflowLayout layout,
        WorkflowPackageMigrationProvenance? migrationProvenance,
        WorkflowPackageDraftPromotionProvenance? draftPromotionProvenance = null)
    {
        var normalized = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        var presentation = WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(layout, normalized), normalized);
        var package = new WorkflowPackage(draftPromotionProvenance is not null ? "1.2" : migrationProvenance is null ? "1.0" : "1.1",
            normalized, presentation, Requirements(normalized), WorkflowJson.ComputeHash(normalized),
            WorkflowLayoutJson.ComputeHash(presentation, normalized), migrationProvenance, draftPromotionProvenance);
        if (migrationProvenance is not null)
            ValidateMigrationProvenance(package);
        if (draftPromotionProvenance is not null)
            ValidateDraftPromotionProvenance(package);
        return package;
    }

    public static ImmutableArray<PackageRequirement> Requirements(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        var requirements = new HashSet<PackageRequirement>();
        foreach (var node in definition.Nodes.Values)
        {
            requirements.Add(new("node", node.Type, node.TypeVersion));
            if (node.WorkflowCall is { } workflowCall)
            {
                var identity = workflowCall.Identity;
                requirements.Add(new("workflow-revision", identity.WorkflowId, identity.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.ContentHash));
                foreach (var requirement in Requirements(workflowCall.Definition)) requirements.Add(requirement);
            }
            if (node.GraphFunction is { } graphFunction)
                requirements.Add(new("graph-function", graphFunction.Definition.FunctionId,
                    graphFunction.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), graphFunction.ContentHash));
            if (node.Tool is { } tool) requirements.Add(new("mcp-tool", tool.BindingId, "", tool.ContractHash) { DescriptionHash = tool.DescriptionHash });
            if (node.Agent is { } agent)
            {
                requirements.Add((definition.SchemaVersion is WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph) && agent.ProviderContractHash is not null
                    ? new("agent-provider-contract", agent.ProviderId, "1", agent.ProviderContractHash)
                    : new("agent-provider", agent.ProviderId, ""));
                foreach (var request in agent.Tools) requirements.Add(new("mcp-tool", request.BindingId, "", request.ContractHash)
                { DescriptionHash = request.DescriptionHash });
                foreach (var skill in agent.Skills ?? []) requirements.Add(new("skill", skill.Id, skill.Version, skill.ContentHash));
                foreach (var profile in agent.Delegates ?? []) requirements.Add(new("agent-profile", profile.Id, profile.Version, profile.ContentHash));
                foreach (var function in agent.GraphFunctionTools ?? [])
                    requirements.Add(new("graph-function-tool", function.Definition.FunctionId,
                        function.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), function.ContentHash));
            }
        }
        foreach (var profile in definition.AgentProfiles?.Values ?? [])
        {
            requirements.Add(new("agent-provider", profile.ProviderId, ""));
            foreach (var tool in profile.Tools) requirements.Add(new("mcp-tool", tool.BindingId, "", tool.ContractHash)
            { DescriptionHash = tool.DescriptionHash });
            foreach (var skill in profile.Skills ?? []) requirements.Add(new("skill", skill.Id, skill.Version, skill.ContentHash));
        }
        return Sort(requirements);
    }

    private static ImmutableArray<PackageRequirement> Sort(IEnumerable<PackageRequirement> requirements) => requirements
        .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)
        .ThenBy(r => r.Version, StringComparer.Ordinal).ThenBy(r => r.ContractHash, StringComparer.Ordinal)
        .ThenBy(r => r.DescriptionHash, StringComparer.Ordinal).ToImmutableArray();

    public static string Serialize(WorkflowDefinition definition, WorkflowLayout layout)
    {
        var json = JsonSerializer.Serialize(Create(definition, layout), Options);
        EnsureSize(json);
        return json;
    }

    public static string Serialize(WorkflowMigrationResult migration)
    {
        var json = JsonSerializer.Serialize(Create(migration), Options);
        EnsureSize(json);
        return json;
    }

    public static string Serialize(WorkflowDraftPromotionResult promotion)
    {
        var json = JsonSerializer.Serialize(Create(promotion), Options);
        EnsureSize(json);
        return json;
    }

    public static string Serialize(WorkflowRevision revision, WorkflowLayout layout)
    {
        var json = JsonSerializer.Serialize(Create(revision, layout), Options);
        EnsureSize(json);
        return json;
    }

    public static string ComputePackageHash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    public static WorkflowPackage Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("Package exceeds 3 MiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var package = JsonSerializer.Deserialize<WorkflowPackage>(json, Options) ?? throw new JsonException("Package required.");
        if (package.FormatVersion is not ("1.0" or "1.1" or "1.2") ||
            package.FormatVersion == "1.0" && (package.MigrationProvenance is not null || package.DraftPromotionProvenance is not null) ||
            package.FormatVersion == "1.1" && (package.MigrationProvenance is null || package.DraftPromotionProvenance is not null) ||
            package.FormatVersion == "1.2" && (package.DraftPromotionProvenance is null || package.MigrationProvenance is not null) ||
            package.Definition is null || package.Layout is null ||
            package.Requirements.IsDefault || package.Requirements.Length > 3000 || package.Requirements.Any(r => r is null))
            throw new ArgumentException("Unsupported or incomplete workflow package.");
        var definition = WorkflowJson.Deserialize(document.RootElement.GetProperty("definition").GetRawText());
        var layout = WorkflowLayoutJson.Deserialize(document.RootElement.GetProperty("layout").GetRawText(), definition);
        var expected = Create(definition, layout, package.MigrationProvenance, package.DraftPromotionProvenance);
        if (!WorkflowJson.MatchesHash(definition, package.DefinitionHash) ||
            !WorkflowLayoutJson.MatchesHash(layout, definition, package.LayoutHash) ||
            package.Requirements.Distinct().Count() != package.Requirements.Length ||
            !Sort(package.Requirements).SequenceEqual(expected.Requirements) ||
            !string.Equals(SerializeMigrationProvenance(package.MigrationProvenance),
                SerializeMigrationProvenance(expected.MigrationProvenance), StringComparison.Ordinal) ||
            !string.Equals(SerializeDraftPromotionProvenance(package.DraftPromotionProvenance),
                SerializeDraftPromotionProvenance(expected.DraftPromotionProvenance), StringComparison.Ordinal))
            throw new ArgumentException("Package checksums or requirements do not match its contents.");
        return expected;
    }

    private static void ValidateMigrationProvenance(WorkflowPackage package)
    {
        var provenance = package.MigrationProvenance!;
        if (string.IsNullOrWhiteSpace(provenance.SourcePackageJson) ||
            Encoding.UTF8.GetByteCount(provenance.SourcePackageJson) > MaximumBytes ||
            !string.Equals(provenance.SourcePackageHash, ComputePackageHash(provenance.SourcePackageJson), StringComparison.Ordinal))
            throw new ArgumentException("Migration source package bytes or hash are invalid.");

        WorkflowPackage source;
        try { source = Deserialize(provenance.SourcePackageJson); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException)
        { throw new ArgumentException("Migration source package is invalid.", exception); }
        if (source.MigrationProvenance is not null ||
            !string.Equals(Serialize(source.Definition, source.Layout), provenance.SourcePackageJson, StringComparison.Ordinal) ||
            source.DefinitionHash != provenance.Revision.SourceContentHash ||
            source.Definition.SchemaVersion != provenance.Revision.SourceSchema ||
            package.DefinitionHash != provenance.Revision.TargetContentHash ||
            package.Definition.SchemaVersion != provenance.Revision.TargetSchema)
            throw new ArgumentException("Migration provenance does not match package contents.");

        var target = WorkflowRevision.Rehydrate(provenance.Revision.TargetRevisionId, package.Definition,
            "package-migration", provenance.Revision.SourceRevisionId, DateTimeOffset.UnixEpoch,
            package.DefinitionHash);
        WorkflowMigrationRegistry.ValidateProvenance(provenance.Revision, target);
    }

    private static void ValidateDraftPromotionProvenance(WorkflowPackage package)
    {
        var provenance = package.DraftPromotionProvenance!;
        if (string.IsNullOrWhiteSpace(provenance.SourceDefinitionJson) ||
            Encoding.UTF8.GetByteCount(provenance.SourceDefinitionJson) > MaximumBytes ||
            string.IsNullOrWhiteSpace(provenance.SourceLayoutJson) ||
            Encoding.UTF8.GetByteCount(provenance.SourceLayoutJson) > MaximumBytes)
            throw new ArgumentException("Draft promotion source bytes are invalid.");
        var sourceDefinition = WorkflowJson.DeserializeDraft(provenance.SourceDefinitionJson);
        var sourceLayout = WorkflowLayoutJson.Deserialize(provenance.SourceLayoutJson, sourceDefinition);
        if (!string.Equals(WorkflowJson.SerializeDraft(sourceDefinition), provenance.SourceDefinitionJson, StringComparison.Ordinal) ||
            !string.Equals(WorkflowLayoutJson.Serialize(sourceLayout, sourceDefinition), provenance.SourceLayoutJson, StringComparison.Ordinal) ||
            sourceDefinition.WorkflowId != provenance.Draft.SourceWorkflowId ||
            WorkflowJson.ComputeDraftHash(sourceDefinition) != provenance.Draft.SourceDefinitionHash ||
            WorkflowLayoutJson.ComputeHash(sourceLayout, sourceDefinition) != provenance.Draft.SourceLayoutHash ||
            package.DefinitionHash != provenance.Draft.TargetContentHash ||
            package.Definition.SchemaVersion != provenance.Draft.TargetSchema)
            throw new ArgumentException("Draft promotion provenance does not match package contents.");
        var target = WorkflowRevision.Rehydrate(provenance.Draft.TargetRevisionId, package.Definition,
            "package-draft-promotion", null, DateTimeOffset.UnixEpoch, package.DefinitionHash,
            draftPromotionProvenance: provenance.Draft);
        provenance.Draft.Validate(target);
    }

    private static string SerializeMigrationProvenance(WorkflowPackageMigrationProvenance? provenance) =>
        provenance is null ? string.Empty : JsonSerializer.Serialize(provenance, Options);

    private static string SerializeDraftPromotionProvenance(WorkflowPackageDraftPromotionProvenance? provenance) =>
        provenance is null ? string.Empty : JsonSerializer.Serialize(provenance, Options);

    private static void EnsureSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("Package exceeds 3 MiB.");
    }
}
