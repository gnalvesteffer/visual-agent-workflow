using System.Collections.Immutable;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioProjectFileTreeNode(string Name, StudioProjectFile? File,
    ImmutableArray<StudioProjectFileTreeNode> Children)
{
    public bool IsDirectory => File is null;
}

/// <summary>Builds a navigable view from manifest-indexed project files only.</summary>
public static class StudioProjectFileTree
{
    public static ImmutableArray<StudioProjectFileTreeNode> Build(IEnumerable<StudioProjectFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = new MutableDirectory("");
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new InvalidDataException("A project file path cannot be empty.");

            var directory = root;
            foreach (var part in parts[..^1])
            {
                if (!directory.Directories.TryGetValue(part, out var child))
                    directory.Directories.Add(part, child = new MutableDirectory(part));
                directory = child;
            }
            directory.Files.Add(new(file, parts[^1]));
        }

        return Freeze(root);
    }

    private static ImmutableArray<StudioProjectFileTreeNode> Freeze(MutableDirectory directory) =>
        directory.Directories.Values.Select(child => new StudioProjectFileTreeNode(child.Name, null, Freeze(child)))
            .Concat(directory.Files.Select(file => new StudioProjectFileTreeNode(file.Name, file.File, [])))
            .OrderBy(node => node.IsDirectory ? 0 : 1)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    private sealed class MutableDirectory(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, MutableDirectory> Directories { get; } = new(StringComparer.Ordinal);
        public List<MutableFile> Files { get; } = [];
    }

    private sealed record MutableFile(StudioProjectFile File, string Name);
}
