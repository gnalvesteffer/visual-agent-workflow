using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderWorkflow(string WorkflowId, string DisplayName, string ContentHash,
    string RelativePath, WorkflowDefinition Definition, WorkflowLayout Layout);

public sealed class StudioFolderWorkflowConflictException(string sourcePath)
    : IOException($"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Reads and saves draft workflow files as project source, without catalog publication history.</summary>
public sealed class StudioFolderWorkflowSource(string projectDirectory)
{
    private static readonly ConcurrentDictionary<string, object> DirectoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
        var gate = DirectoryLocks.GetOrAdd(root, static _ => new object());
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

        var absoluteAssetPath = StudioProjectSourceDirectory.ResolvePath(root, relativePath);
        WriteContentAddressedFile(absoluteAssetPath, bytes, contentHash);

        var manifestPath = Path.Combine(root, StudioProjectSourceArchive.ManifestFileName);
        var originalManifestBytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(manifest));
        EnsureCurrentProject(StudioProjectSourceArchive.ComputeContentHash(originalManifestBytes));
        var writtenAsset = StudioProjectSourceDirectory.ReadBounded(absoluteAssetPath,
            StudioProjectSourceArchive.MaximumFileBytes, "A project source file exceeds the supported size.");
        if (StudioProjectSourceArchive.ComputeContentHash(writtenAsset) != contentHash)
            throw new StudioFolderWorkflowConflictException(definition.WorkflowId);
        var updatedManifestBytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(updatedManifest));
        ReplaceManifestAtomically(manifestPath, updatedManifestBytes);

        // Leave old content-addressed source files in place. A reader that loaded the previous
        // manifest immediately before the atomic pointer swap may still be opening that asset.
        return ToWorkflow(file, bytes);
    }

    private void EnsureCurrentProject(string expectedManifestHash)
    {
        var (currentManifest, _) = StudioProjectSourceDirectory.Read(root);
        var normalized = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(currentManifest));
        if (StudioProjectSourceArchive.ComputeContentHash(normalized) != expectedManifestHash)
            throw new StudioFolderWorkflowConflictException("project manifest");
    }

    private void WriteContentAddressedFile(string destination, byte[] content, string expectedHash)
    {
        var parent = Path.GetDirectoryName(destination)!;
        StudioProjectSourceDirectory.EnsureSafeDirectories(root, parent);
        if (File.Exists(destination))
        {
            StudioProjectSourceDirectory.RejectPathLinks(root, destination);
            var existing = StudioProjectSourceDirectory.ReadBounded(destination,
                StudioProjectSourceArchive.MaximumFileBytes, "A project source file exceeds the supported size.");
            if (StudioProjectSourceArchive.ComputeContentHash(existing) != expectedHash)
                throw new InvalidDataException("A content-addressed project source path contains different content.");
            return;
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(content);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ReplaceManifestAtomically(string manifestPath, byte[] content)
    {
        var temporary = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(content);
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static StudioFolderWorkflow ToWorkflow(StudioProjectFile file, byte[] content)
    {
        var source = StudioProjectSourceArchive.DeserializeWorkflowSource(content);
        return new(source.Definition.WorkflowId, source.Definition.DisplayName ?? source.Definition.WorkflowId,
            file.Sha256, file.Path, source.Definition, source.Layout);
    }
}
