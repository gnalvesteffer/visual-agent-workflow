using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderAgentProfile(string ContentHash, string RelativePath, AgentProfileLibraryItem Item);

public sealed class StudioFolderAgentProfileConflictException(string sourcePath)
    : IOException($"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Lists and creates immutable agent profile revisions in the project folder only.</summary>
public sealed class StudioFolderAgentProfileSource(string projectDirectory) : IAgentProfileLibrary
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<StudioFolderAgentProfile> List()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "agent-profile")
            .Select(file => ToProfile(file, StudioProjectSourceArchive.DeserializeAgentProfileAsset(files[file.Path])))
            .OrderBy(profile => profile.Item.Folder, StringComparer.Ordinal)
            .ThenBy(profile => profile.Item.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Item.Profile.Version, StringComparer.Ordinal)
            .ToArray();
    }

    IReadOnlyList<AgentProfileLibraryItem> IAgentProfileLibrary.List() => List().Select(profile => profile.Item).ToArray();

    public StudioFolderAgentProfile SaveNewVersion(AgentProfile profile, string folder, string expectedManifestHash,
        string? targetDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestHash);
        StudioAgentProfileLibrary.Validate(profile, folder);

        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, existingFiles) = StudioProjectSourceDirectory.Read(root);
            if (StudioProjectSourceArchive.ComputeManifestHash(manifest) != expectedManifestHash)
                throw new StudioFolderAgentProfileConflictException("project manifest");
            if (manifest.Files.Any(file => file.Kind == "agent-profile" && file.Id == profile.Id && file.Version == profile.Version))
                throw new ArgumentException("This agent profile version already exists. Use a new version for edits.");

            var item = new AgentProfileLibraryItem(profile, folder);
            var bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeAgentProfileAsset(item));
            if (bytes.Length > StudioProjectSourceArchive.MaximumFileBytes)
                throw new InvalidDataException("Project agent profile exceeds the supported source file size.");

            var contentHash = StudioProjectSourceArchive.ComputeContentHash(bytes);
            var relativePath = StudioProjectSourceArchive.AssetPathInDirectory(targetDirectory ?? "agents",
                StudioProjectSourceArchive.SafeAssetFileStem(profile.Id + "-" + profile.Version), ".agent", contentHash);
            var file = new StudioProjectFile("agent-profile", profile.Id, profile.Version, relativePath, contentHash);
            var updatedManifest = manifest with
            {
                FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Files = manifest.Files.Append(file).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray(),
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(
                    manifest.Files.Select(entry => entry.Path).Append(relativePath), manifest.Directories)
            };
            var updatedFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var existing in existingFiles) updatedFiles.Add(existing.Key, existing.Value);
            updatedFiles.Add(relativePath, bytes);
            _ = StudioProjectSourceArchive.CreateArchive(updatedManifest, updatedFiles);

            StudioFolderSourceWriter.PublishImmutableAsset(root, expectedManifestHash, updatedManifest,
                relativePath, bytes, contentHash, sourcePath => new StudioFolderAgentProfileConflictException(sourcePath));
            return ToProfile(file, item);
        }
    }

    private static StudioFolderAgentProfile ToProfile(StudioProjectFile file, AgentProfileLibraryItem item) =>
        new(file.Sha256, file.Path, item);
}
