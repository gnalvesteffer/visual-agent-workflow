using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderSkill(string ContentHash, string RelativePath, SkillLibraryItem Item);

public sealed class StudioFolderSkillConflictException(string sourcePath)
    : IOException($"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Reads and adds immutable skill revisions directly to a project folder without catalog writes.</summary>
public sealed class StudioFolderSkillSource(string projectDirectory)
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<StudioFolderSkill> List()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "skill")
            .Select(file => ToSkill(file, StudioProjectSourceArchive.DeserializeSkillAsset(files[file.Path])))
            .OrderBy(skill => skill.Item.Folder, StringComparer.Ordinal)
            .ThenBy(skill => skill.Item.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Item.Skill.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public StudioFolderSkill SaveNewVersion(string id, string version, string name, string instructions, string folder,
        string expectedManifestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestHash);
        StudioSkillLibrary.Validate(id, version, name, instructions, folder);

        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, existingFiles) = StudioProjectSourceDirectory.Read(root);
            if (StudioProjectSourceArchive.ComputeManifestHash(manifest) != expectedManifestHash)
                throw new StudioFolderSkillConflictException("project manifest");
            if (manifest.Files.Any(file => file.Kind == "skill" && file.Id == id && file.Version == version))
                throw new ArgumentException("This skill version already exists. Use a new version for edited instructions.");

            var item = new SkillLibraryItem(WorkflowSkill.Create(id, version, name, instructions), folder);
            var bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(item));
            if (bytes.Length > StudioProjectSourceArchive.MaximumFileBytes)
                throw new InvalidDataException("Project skill exceeds the supported source file size.");

            var contentHash = StudioProjectSourceArchive.ComputeContentHash(bytes);
            var relativePath = StudioProjectSourceArchive.SkillAssetPath(id, version, contentHash);
            var file = new StudioProjectFile("skill", id, version, relativePath, contentHash);
            var updatedManifest = manifest with
            {
                Files = manifest.Files.Append(file).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray()
            };
            var updatedFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var existing in existingFiles) updatedFiles.Add(existing.Key, existing.Value);
            updatedFiles.Add(relativePath, bytes);
            _ = StudioProjectSourceArchive.CreateArchive(updatedManifest, updatedFiles);

            StudioFolderSourceWriter.PublishImmutableAsset(root, expectedManifestHash, updatedManifest,
                relativePath, bytes, contentHash, sourcePath => new StudioFolderSkillConflictException(sourcePath));
            return ToSkill(file, item);
        }
    }

    private static StudioFolderSkill ToSkill(StudioProjectFile file, SkillLibraryItem item) =>
        new(file.Sha256, file.Path, item);
}
