using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public enum StudioFolderDependencyIssueKind
{
    Missing,
    Stale,
    Invalid
}

public sealed record StudioFolderDependencyIssue(
    StudioFolderDependencyIssueKind Kind, string AssetKind, string Id, string Version, string Message);

/// <summary>
/// Compares immutable dependencies embedded in a workflow with the current folder source.
/// It reports drift only; it never replaces embedded snapshots or resolves live runtime assets.
/// </summary>
public static class StudioFolderDependencyAudit
{
    private delegate T AssetDeserializer<T>(ReadOnlySpan<byte> bytes);

    public static ImmutableArray<StudioFolderDependencyIssue> Assess(
        WorkflowDefinition definition, StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        var issues = ImmutableArray.CreateBuilder<StudioFolderDependencyIssue>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in definition.Skills?.Values ?? [])
            CheckSkill(skill.Id, skill.Version, skill.ContentHash, manifest, files, issues);
        foreach (var profile in definition.AgentProfiles?.Values ?? [])
            CheckProfile(profile, manifest, files, issues);
        foreach (var node in definition.Nodes.Values)
        {
            if (node.GraphFunction is { } function)
                CheckFunction(function, manifest, files, issues, visited);
            if (node.Agent is { } agent)
            {
                foreach (var skill in agent.Skills ?? [])
                    CheckSkill(skill.Id, skill.Version, skill.ContentHash, manifest, files, issues);
                foreach (var profile in agent.Delegates ?? [])
                {
                    var key = profile.Id + "@" + profile.Version;
                    if (definition.AgentProfiles?.TryGetValue(key, out var embedded) == true)
                        CheckProfile(embedded, manifest, files, issues);
                    else
                        Add(issues, StudioFolderDependencyIssueKind.Missing, "agent-profile", profile.Id, profile.Version,
                            "The workflow references an embedded agent profile that is not present in the definition.");
                }
                foreach (var graphFunction in agent.GraphFunctionTools ?? [])
                    CheckFunction(graphFunction, manifest, files, issues, visited);
            }
        }
        return issues.ToImmutable();
    }

    private static void CheckProfile(AgentProfile profile, StudioProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files, ImmutableArray<StudioFolderDependencyIssue>.Builder issues)
    {
        CheckAsset("agent-profile", profile.Id, profile.Version, profile.ContentHash, manifest, files, issues,
            StudioProjectSourceArchive.DeserializeAgentProfileAsset,
            item => item.Profile.ContentHash);
        foreach (var skill in profile.Skills ?? [])
            CheckSkill(skill.Id, skill.Version, skill.ContentHash, manifest, files, issues);
    }

    private static void CheckSkill(string id, string version, string expectedHash, StudioProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files, ImmutableArray<StudioFolderDependencyIssue>.Builder issues) =>
        CheckAsset("skill", id, version, expectedHash, manifest, files, issues,
            StudioProjectSourceArchive.DeserializeSkillAsset,
            item => item.Skill.ContentHash);

    private static void CheckFunction(GraphFunctionConfiguration function, StudioProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files, ImmutableArray<StudioFolderDependencyIssue>.Builder issues,
        HashSet<string> visited)
    {
        var key = function.Definition.FunctionId + "@" + function.Definition.Version;
        if (!visited.Add(key)) return;
        CheckAsset("graph-function", function.Definition.FunctionId,
            function.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), function.ContentHash,
            manifest, files, issues,
            StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset,
            item => item.Item.Function.ContentHash);
    }

    private static void CheckAsset<T>(string kind, string id, string version, string? expectedHash,
        StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files,
        ImmutableArray<StudioFolderDependencyIssue>.Builder issues,
        AssetDeserializer<T> deserialize, Func<T, string?> hash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            Add(issues, StudioFolderDependencyIssueKind.Invalid, kind, id, version,
                "The workflow dependency does not contain an embedded content hash.");
            return;
        }
        var matches = manifest.Files.Where(file => file.Kind == kind && file.Id == id && file.Version == version).ToArray();
        if (matches.Length == 0)
        {
            Add(issues, StudioFolderDependencyIssueKind.Missing, kind, id, version,
                $"The folder has no source asset for {kind} '{id}@{version}'.");
            return;
        }
        if (matches.Length > 1)
        {
            Add(issues, StudioFolderDependencyIssueKind.Invalid, kind, id, version,
                $"The folder contains multiple source assets for {kind} '{id}@{version}'.");
            return;
        }
        var file = matches[0];
        if (!files.TryGetValue(file.Path, out var content))
        {
            Add(issues, StudioFolderDependencyIssueKind.Missing, kind, id, version,
                $"The indexed source file '{file.Path}' is missing.");
            return;
        }
        try
        {
            var actualHash = hash(deserialize(content));
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                Add(issues, StudioFolderDependencyIssueKind.Stale, kind, id, version,
                    $"The source asset '{file.Path}' differs from the workflow's embedded content hash.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        {
            Add(issues, StudioFolderDependencyIssueKind.Invalid, kind, id, version,
                $"The source asset '{file.Path}' is invalid: {exception.Message}");
        }
    }

    private static void Add(ImmutableArray<StudioFolderDependencyIssue>.Builder issues,
        StudioFolderDependencyIssueKind kind, string assetKind, string id, string version, string message) =>
        issues.Add(new(kind, assetKind, id, version, message));
}
