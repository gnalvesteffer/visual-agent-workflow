namespace VisualAgentWorkflow.Studio;

/// <summary>Reads and writes validated project source files without extracting untrusted archives.</summary>
public static class StudioProjectSourceDirectory
{
    private const string GitIgnoreContents = ".vaw/\n";

    public static (StudioProjectManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The project source folder does not exist.");
        RejectReparsePoint(root);

        var manifestPath = Path.Combine(root, StudioProjectSourceArchive.ManifestFileName);
        RejectReparsePoint(manifestPath);
        var manifest = StudioProjectSourceArchive.DeserializeManifest(ReadBounded(manifestPath));
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in manifest.Files)
        {
            var assetPath = ResolvePath(root, asset.Path);
            RejectPathLinks(root, assetPath);
            files.Add(asset.Path, ReadBounded(assetPath));
        }

        var validationArchive = StudioProjectSourceArchive.CreateArchive(manifest, files);
        return StudioProjectSourceArchive.Read(validationArchive);
    }

    /// <summary>Creates a fresh portable source folder. Existing directories are never overwritten.</summary>
    public static void Write(string directory, StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(directory);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("Choose a folder path that does not already exist.");

        // Reuse archive validation to ensure the folder and archive formats have identical content rules.
        var (normalizedManifest, normalizedFiles) = StudioProjectSourceArchive.Read(
            StudioProjectSourceArchive.CreateArchive(manifest, files));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in normalizedFiles.OrderBy(file => file.Key, StringComparer.Ordinal))
                WriteAtomically(root, file.Key, file.Value);
            WriteAtomically(root, StudioProjectSourceArchive.ManifestFileName,
                System.Text.Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(normalizedManifest)));
            WriteAtomically(root, ".gitignore", System.Text.Encoding.UTF8.GetBytes(GitIgnoreContents));
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static string ResolvePath(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A project asset resolves outside the project folder.");
        return candidate;
    }

    private static void RejectPathLinks(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Project source files must not be symbolic links or reparse points.");
    }

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"Required project source file '{Path.GetFileName(path)}' is missing.");
        if (info.Length is <= 0 or > StudioProjectSourceArchive.MaximumFileBytes)
            throw new InvalidDataException("A project source file exceeds the supported size.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream((int)info.Length);
        input.CopyTo(output);
        if (output.Length != info.Length) throw new InvalidDataException("A project source file changed while it was being read.");
        return output.ToArray();
    }

    private static void WriteAtomically(string root, string relativePath, byte[] contents)
    {
        var path = ResolvePath(root, relativePath);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(contents);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
