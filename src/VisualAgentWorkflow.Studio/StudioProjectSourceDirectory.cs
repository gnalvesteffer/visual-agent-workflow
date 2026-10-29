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
        StudioFolderSourceWriter.RecoverPendingWorkflowSave(root);

        var manifestPath = Path.Combine(root, StudioProjectSourceArchive.ManifestFileName);
        RejectReparsePoint(manifestPath);
        var manifestBytes = ReadBounded(manifestPath, StudioProjectSourceArchive.MaximumFileBytes,
            "The project manifest exceeds the supported size.");
        var manifest = StudioProjectSourceArchive.DeserializeManifest(manifestBytes);
        long totalBytes = manifestBytes.Length;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in manifest.Files)
        {
            var assetPath = ResolvePath(root, asset.Path);
            RejectPathLinks(root, assetPath);
            var assetInfo = new FileInfo(assetPath);
            if (!assetInfo.Exists) throw new InvalidDataException($"Required project source file '{asset.Path}' is missing.");
            if (assetInfo.Length > StudioProjectSourceArchive.MaximumFileBytes)
                throw new InvalidDataException("A project source file exceeds the supported size.");
            if (totalBytes + assetInfo.Length > StudioProjectSourceArchive.MaximumArchiveBytes)
                throw new InvalidDataException("Project source folder exceeds the supported aggregate size.");

            var remainingBytes = StudioProjectSourceArchive.MaximumArchiveBytes - checked((int)totalBytes);
            var content = ReadBounded(assetPath, Math.Min(StudioProjectSourceArchive.MaximumFileBytes, remainingBytes),
                "Project source folder exceeds the supported aggregate size.");
            totalBytes += content.Length;
            files.Add(asset.Path, content);
        }

        var refreshedManifest = StudioProjectSourceArchive.RefreshMutableWorkflowHashes(manifest, files);
        refreshedManifest = StudioProjectSourceArchive.RefreshMutableReusableAssetHashes(refreshedManifest, files);
        if (!refreshedManifest.Equals(manifest)) ReplaceManifest(root, refreshedManifest);
        var validationArchive = StudioProjectSourceArchive.CreateArchive(refreshedManifest, files);
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
            if (!normalizedManifest.Directories.IsDefault)
                foreach (var directoryPath in normalizedManifest.Directories)
                    EnsureSafeDirectories(root, ResolvePath(root, directoryPath));
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

    internal static string ResolvePath(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A project asset resolves outside the project folder.");
        return candidate;
    }

    internal static void RejectPathLinks(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    internal static void EnsureSafeDirectories(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A project source directory resolves outside the project folder.");
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) RejectReparsePoint(current);
            else Directory.CreateDirectory(current);
        }
    }

    internal static void ReplaceManifest(string root, StudioProjectManifest manifest)
    {
        var path = Path.Combine(root, StudioProjectSourceArchive.ManifestFileName);
        RejectPathLinks(root, path);
        var bytes = System.Text.Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(manifest));
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void WriteIndexedFile(string root, string relativePath, byte[] contents)
    {
        var path = ResolvePath(root, relativePath);
        EnsureSafeDirectories(root, Path.GetDirectoryName(path)!);
        RejectPathLinks(root, Path.GetDirectoryName(path)!);
        if (File.Exists(path)) RejectPathLinks(root, path);
        if (File.Exists(path)) throw new IOException("A project file already exists at the requested path.");
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

    internal static void ReplaceIndexedFile(string root, string relativePath, byte[] contents)
    {
        var path = ResolvePath(root, relativePath);
        EnsureSafeDirectories(root, Path.GetDirectoryName(path)!);
        RejectPathLinks(root, Path.GetDirectoryName(path)!);
        if (File.Exists(path)) RejectPathLinks(root, path);
        if (!File.Exists(path)) throw new IOException("The selected project file no longer exists.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Project source files must not be symbolic links or reparse points.");
    }

    internal static byte[] ReadBounded(string path, int maximumBytes, string sizeError)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"Required project source file '{Path.GetFileName(path)}' is missing.");
        if (info.Length <= 0 || info.Length > maximumBytes) throw new InvalidDataException(sizeError);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var openedLength = input.Length;
        if (openedLength <= 0 || openedLength > maximumBytes) throw new InvalidDataException(sizeError);
        using var output = new MemoryStream((int)openedLength);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException(sizeError);
            output.Write(buffer, 0, read);
        }
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
