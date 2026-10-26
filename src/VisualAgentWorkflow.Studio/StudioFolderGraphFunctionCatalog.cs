using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Read-only function snapshots available to workflow editing in folder mode.</summary>
public sealed class StudioFolderGraphFunctionCatalog(string projectDirectory) : IGraphFunctionCatalog
{
    private readonly string root = Path.GetFullPath(projectDirectory);

    public IReadOnlyList<GraphFunctionCatalogEntry> List()
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(root);
        return manifest.Files.Where(file => file.Kind == "graph-function")
            .Select(file => StudioProjectSourceArchive.DeserializeGraphFunctionAsset(files[file.Path]))
            .OrderBy(item => item.Folder, StringComparer.Ordinal)
            .ThenBy(item => item.Function.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Function.Definition.Version)
            .ToArray();
    }
}
