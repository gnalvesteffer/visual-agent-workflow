using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioFolderGraphFunction(string ContentHash, string RelativePath, GraphFunctionCatalogEntry Item, WorkflowLayout Layout);

public sealed class StudioFolderGraphFunctionConflictException(string sourcePath)
    : IOException($"Project source '{sourcePath}' changed in the project folder. Reload it before saving.");

/// <summary>Reads and creates immutable pure graph-function revisions in a project folder.</summary>
public sealed class StudioFolderGraphFunctionCatalog(string projectDirectory) : IGraphFunctionCatalog
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<StudioFolderGraphFunction> ListAssets()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "graph-function")
            .Select(file =>
            {
                var asset = StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(files[file.Path]);
                return new StudioFolderGraphFunction(file.Sha256, file.Path, asset.Item, asset.Layout);
            })
            .OrderBy(function => function.Item.Folder, StringComparer.Ordinal)
            .ThenBy(function => function.Item.Function.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(function => function.Item.Function.Definition.Version)
            .ToArray();
    }

    public IReadOnlyList<GraphFunctionCatalogEntry> List() => ListAssets().Select(function => function.Item).ToArray();

    public StudioFolderGraphFunction SaveNewVersion(GraphFunctionDefinition definition, string folder,
        string expectedManifestHash, WorkflowLayout? layout = null, string? targetDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestHash);
        StudioGraphFunctionLibrary.Validate(definition, folder);

        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, existingFiles) = StudioProjectSourceDirectory.Read(root);
            if (StudioProjectSourceArchive.ComputeManifestHash(manifest) != expectedManifestHash)
                throw new StudioFolderGraphFunctionConflictException("project manifest");

            var version = definition.Version.ToString(CultureInfo.InvariantCulture);
            if (manifest.Files.Any(file => file.Kind == "graph-function" && file.Id == definition.FunctionId && file.Version == version))
                throw new ArgumentException("This graph function version already exists. Use a new version for edits.");

            var configuration = new GraphFunctionConfiguration(definition, GraphFunctionJson.ComputeHash(definition));
            var item = new GraphFunctionCatalogEntry(configuration, folder);
            var normalizedAsset = StudioGraphFunctionAsset.Create(item, layout);
            var bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeGraphFunctionAsset(item, normalizedAsset.Layout));
            if (bytes.Length > StudioProjectSourceArchive.MaximumFileBytes)
                throw new InvalidDataException("Project graph function exceeds the supported source file size.");

            var contentHash = StudioProjectSourceArchive.ComputeContentHash(bytes);
            var relativePath = targetDirectory is null
                ? StudioProjectSourceArchive.VersionedAssetPath("functions", definition.FunctionId, version, contentHash)
                : StudioProjectSourceArchive.AssetPathInDirectory(targetDirectory,
                    StudioProjectSourceArchive.SafeAssetFileStem(definition.FunctionId + "-" + version), ".function", contentHash);
            var file = new StudioProjectFile("graph-function", definition.FunctionId, version, relativePath, contentHash);
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
                relativePath, bytes, contentHash, sourcePath => new StudioFolderGraphFunctionConflictException(sourcePath));
            return new(contentHash, relativePath, item, normalizedAsset.Layout);
        }
    }
}
