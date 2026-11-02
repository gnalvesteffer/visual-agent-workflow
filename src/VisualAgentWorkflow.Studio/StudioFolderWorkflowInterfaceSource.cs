using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderWorkflowInterface(string ContentHash, string RelativePath,
    WorkflowInterfaceRevision Revision);

public sealed record StudioFolderWorkflowInterfaceImplementation(string ContentHash, string RelativePath,
    WorkflowInterfaceImplementationRevision Revision);

public sealed class StudioFolderWorkflowInterfaceConflictException(string sourcePath)
    : IOException($"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Reads and adds immutable interface and implementation revisions to a folder project.</summary>
public sealed class StudioFolderWorkflowInterfaceSource(string projectDirectory)
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<StudioFolderWorkflowInterface> ListInterfaces()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "workflow-interface")
            .Select(file => new StudioFolderWorkflowInterface(file.Sha256, file.Path,
                WorkflowInterfaceJson.DeserializeRevision(Encoding.UTF8.GetString(files[file.Path]))))
            .OrderBy(asset => asset.Revision.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.Revision.Version).ToArray();
    }

    public IReadOnlyList<StudioFolderWorkflowInterfaceImplementation> ListImplementations()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "workflow-interface-implementation")
            .Select(file => new StudioFolderWorkflowInterfaceImplementation(file.Sha256, file.Path,
                WorkflowInterfaceJson.DeserializeImplementationRevision(Encoding.UTF8.GetString(files[file.Path]))))
            .OrderBy(asset => asset.Revision.Definition.ImplementationId, StringComparer.Ordinal)
            .ThenBy(asset => asset.Revision.Version).ToArray();
    }

    public StudioFolderWorkflowInterface SaveInterfaceRevision(WorkflowInterfaceRevision revision, string expectedManifestHash)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        var id = revision.Definition.InterfaceId;
        var version = revision.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = WorkflowInterfaceJson.Serialize(revision);
        var saved = Save("workflow-interface", "interfaces", id, version, json, expectedManifestHash,
            file => WorkflowInterfaceJson.DeserializeRevision(Encoding.UTF8.GetString(file)));
        return new(saved.File.Sha256, saved.File.Path, saved.Revision);
    }

    public StudioFolderWorkflowInterfaceImplementation SaveImplementationRevision(
        WorkflowInterfaceImplementationRevision revision, string expectedManifestHash)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        var id = revision.Definition.ImplementationId;
        var version = revision.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = WorkflowInterfaceJson.Serialize(revision);
        var saved = Save("workflow-interface-implementation", "implementations", id, version, json,
            expectedManifestHash, file => WorkflowInterfaceJson.DeserializeImplementationRevision(Encoding.UTF8.GetString(file)));
        return new(saved.File.Sha256, saved.File.Path, saved.Revision);
    }

    private (StudioProjectFile File, TRevision Revision) Save<TRevision>(string kind, string directory, string id,
        string version, string json, string expectedManifestHash, Func<byte[], TRevision> deserialize)
        where TRevision : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestHash);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, existingFiles) = StudioProjectSourceDirectory.Read(root);
            if (StudioProjectSourceArchive.ComputeManifestHash(manifest) != expectedManifestHash)
                throw new StudioFolderWorkflowInterfaceConflictException("project manifest");
            if (manifest.Files.Any(file => file.Kind == kind && file.Id == id && file.Version == version))
                throw new ArgumentException("This workflow interface asset version already exists. Create a new revision.");

            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > StudioProjectSourceArchive.MaximumFileBytes)
                throw new InvalidDataException("Workflow interface asset exceeds the supported source file size.");
            var contentHash = StudioProjectSourceArchive.ComputeContentHash(bytes);
            var path = StudioProjectSourceArchive.VersionedAssetPath(directory, id, version, contentHash);
            var file = new StudioProjectFile(kind, id, version, path, contentHash);
            var updatedManifest = manifest with
            {
                FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Files = manifest.Files.Append(file).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray(),
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(
                    manifest.Files.Select(entry => entry.Path).Append(path), manifest.Directories)
            };
            var updatedFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var existing in existingFiles) updatedFiles.Add(existing.Key, existing.Value);
            updatedFiles.Add(path, bytes);
            _ = StudioProjectSourceArchive.CreateArchive(updatedManifest, updatedFiles);
            StudioFolderSourceWriter.PublishImmutableAsset(root, expectedManifestHash, updatedManifest,
                path, bytes, contentHash, sourcePath => new StudioFolderWorkflowInterfaceConflictException(sourcePath));
            return (file, deserialize(bytes));
        }
    }
}
