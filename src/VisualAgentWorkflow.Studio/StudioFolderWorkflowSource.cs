using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderWorkflow(string WorkflowId, string DisplayName, string ContentHash,
    string RelativePath, WorkflowDefinition Definition, WorkflowLayout Layout);

public sealed class StudioFolderWorkflowConflictException(string sourcePath, string? action = null)
    : IOException(action ?? $"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Reads and saves draft workflow files as project source, without catalog publication history.</summary>
public sealed class StudioFolderWorkflowSource(string projectDirectory)
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<StudioFolderWorkflow> List()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "workflow")
            .Select(file => ToWorkflow(file, files[file.Path]))
            .OrderBy(workflow => workflow.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .ToArray();
    }

    public StudioFolderWorkflow? Get(string workflowId) => List().SingleOrDefault(workflow => workflow.WorkflowId == workflowId);

    public StudioFolderWorkflow Save(WorkflowDefinition definition, WorkflowLayout layout, string? expectedContentHash)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(layout);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate) return SaveLocked(definition, layout, expectedContentHash);
    }

    private StudioFolderWorkflow SaveLocked(WorkflowDefinition definition, WorkflowLayout layout, string? expectedContentHash)
    {
        var (manifest, existingFiles) = StudioProjectSourceDirectory.Read(root);
        var currentFile = manifest.Files.SingleOrDefault(file => file.Kind == "workflow" && file.Id == definition.WorkflowId);
        if (currentFile is null ? expectedContentHash is not null : expectedContentHash != currentFile.Sha256)
            throw new StudioFolderWorkflowConflictException(definition.WorkflowId);

        var bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeWorkflowSource(definition, layout));
        var source = StudioProjectSourceArchive.DeserializeWorkflowSource(bytes);
        if (source.Definition.WorkflowId != definition.WorkflowId)
            throw new InvalidDataException("The workflow source identity changed during serialization.");
        var contentHash = StudioProjectSourceArchive.ComputeContentHash(bytes);
        var relativePath = StudioProjectSourceArchive.WorkflowDraftPath(definition.WorkflowId, contentHash);
        var file = new StudioProjectFile("workflow", definition.WorkflowId, "draft", relativePath, contentHash);
        if (currentFile?.Sha256 == contentHash)
            return ToWorkflow(currentFile, existingFiles[currentFile.Path]);

        var updatedManifest = manifest with
        {
            Files = manifest.Files.Where(existing => existing != currentFile).Append(file)
                .OrderBy(existing => existing.Path, StringComparer.Ordinal).ToImmutableArray()
        };
        var updatedFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var existing in existingFiles) updatedFiles.Add(existing.Key, existing.Value);
        if (currentFile is not null) updatedFiles.Remove(currentFile.Path);
        updatedFiles.Add(relativePath, bytes);
        _ = StudioProjectSourceArchive.CreateArchive(updatedManifest, updatedFiles);

        StudioFolderSourceWriter.PublishImmutableAsset(root, StudioProjectSourceArchive.ComputeManifestHash(manifest),
            updatedManifest, relativePath, bytes, contentHash,
            sourcePath => new StudioFolderWorkflowConflictException(sourcePath));

        // Leave old content-addressed source files in place. A reader that loaded the previous
        // manifest immediately before the atomic pointer swap may still be opening that asset.
        return ToWorkflow(file, bytes);
    }

    private static StudioFolderWorkflow ToWorkflow(StudioProjectFile file, byte[] content)
    {
        var source = StudioProjectSourceArchive.DeserializeWorkflowSource(content);
        return new(source.Definition.WorkflowId, source.Definition.DisplayName ?? source.Definition.WorkflowId,
            file.Sha256, file.Path, source.Definition, source.Layout);
    }
}
