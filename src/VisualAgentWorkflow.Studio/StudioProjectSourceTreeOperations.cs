using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Manifest-first file operations for the Studio's manifest-indexed project Explorer.</summary>
public static class StudioProjectSourceTreeOperations
{
    public static StudioProjectManifest CreateDirectory(string root, string path, string expectedManifestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceArchive.ValidateProjectPath(path);
            if (manifest.Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project file already uses that path.");
            if (Directories(manifest).Any(directory => string.Equals(directory, path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project folder already uses that path.");
            StudioProjectSourceDirectory.EnsureSafeDirectories(root, StudioProjectSourceDirectory.ResolvePath(root, path));
            var directories = StudioProjectSourceArchive.DirectoriesForPaths([path + "/_folder_entry.workflow"], Directories(manifest));
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion, Directories = directories };
            ValidateAndWrite(root, updated, files);
            return updated;
        }
    }

    public static StudioProjectManifest RenameDirectory(string root, string oldPath, string newPath, string expectedManifestHash)
    {
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceArchive.ValidateProjectPath(oldPath);
            StudioProjectSourceArchive.ValidateProjectPath(newPath);
            if (!Directories(manifest).Contains(oldPath, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The selected project folder no longer exists.");
            var oldPhysicalPath = StudioProjectSourceDirectory.ResolvePath(root, oldPath);
            if (Directory.Exists(oldPhysicalPath)) StudioProjectSourceDirectory.RejectPathLinks(root, oldPhysicalPath);
            if (newPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A folder cannot be moved inside itself.");

            var movedDirectories = Directories(manifest).Where(path => path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var movedFiles = manifest.Files.Where(file => file.Path.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var filesOutside = manifest.Files.Except(movedFiles).ToArray();
            var directoriesOutside = Directories(manifest).Except(movedDirectories, StringComparer.OrdinalIgnoreCase).ToArray();
            var replacements = movedFiles.ToDictionary(file => file.Path,
                file => newPath + file.Path[oldPath.Length..], StringComparer.Ordinal);
            if (replacements.Values.Any(path => filesOutside.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("The destination contains a file with the same name.");
            if (filesOutside.Any(file => string.Equals(file.Path, newPath, StringComparison.OrdinalIgnoreCase) ||
                newPath.StartsWith(file.Path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project file blocks the destination folder.");
            if (directoriesOutside.Any(path => string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(newPath + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project folder already exists at the destination.");

            var updatedFiles = filesOutside.Select(file => file with { }).Concat(movedFiles.Select(file => file with { Path = replacements[file.Path] }))
                .OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray();
            var renamedDirectories = movedDirectories.Select(directory => newPath + directory[oldPath.Length..]);
            var directories = StudioProjectSourceArchive.DirectoriesForPaths(
                updatedFiles.Select(file => file.Path).Append(newPath + "/_folder_entry.workflow"),
                directoriesOutside.Concat(renamedDirectories));
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion, Files = updatedFiles, Directories = directories };
            try
            {
                foreach (var file in movedFiles) StudioProjectSourceDirectory.WriteIndexedFile(root, replacements[file.Path], files[file.Path]);
                ValidateAndWrite(root, updated, filesOutside.ToDictionary(file => file.Path, file => files[file.Path], StringComparer.Ordinal)
                .Concat(movedFiles.ToDictionary(file => replacements[file.Path], file => files[file.Path], StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }
            catch
            {
                foreach (var movedPath in replacements.Values) TryDelete(StudioProjectSourceDirectory.ResolvePath(root, movedPath));
                throw;
            }
            foreach (var directory in directories)
                StudioProjectSourceDirectory.EnsureSafeDirectories(root, StudioProjectSourceDirectory.ResolvePath(root, directory));
            foreach (var file in movedFiles) TryDelete(StudioProjectSourceDirectory.ResolvePath(root, file.Path));
            return updated;
        }
    }

    public static StudioProjectManifest DeleteEmptyDirectory(string root, string path, string expectedManifestHash)
    {
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceArchive.ValidateProjectPath(path);
            if (!Directories(manifest).Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The selected project folder no longer exists.");
            var physicalPath = StudioProjectSourceDirectory.ResolvePath(root, path);
            if (Directory.Exists(physicalPath)) StudioProjectSourceDirectory.RejectPathLinks(root, physicalPath);
            if (manifest.Files.Any(file => file.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) ||
                Directories(manifest).Any(directory => directory.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This folder is not empty. Move or delete its contents first.");
            var updated = manifest with
            {
                FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Directories = Directories(manifest).Where(directory => !string.Equals(directory, path, StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
            };
            ValidateAndWrite(root, updated, files);
            if (Directory.Exists(physicalPath) && !Directory.EnumerateFileSystemEntries(physicalPath).Any())
                Directory.Delete(physicalPath);
            return updated;
        }
    }

    public static StudioProjectManifest MoveFile(string root, string oldPath, string newPath, string expectedManifestHash)
    {
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceArchive.ValidateProjectPath(newPath);
            var source = manifest.Files.SingleOrDefault(file => string.Equals(file.Path, oldPath, StringComparison.Ordinal))
                ?? throw new IOException("The selected project file no longer exists.");
            if (Path.GetExtension(newPath) is { } extension && !string.Equals(extension, Path.GetExtension(oldPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Rename the file without changing its asset extension.");
            if (manifest.Files.Any(file => string.Equals(file.Path, newPath, StringComparison.OrdinalIgnoreCase)) ||
                Directories(manifest).Any(directory => string.Equals(directory, newPath, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project file or folder already uses that path.");

            StudioProjectSourceDirectory.WriteIndexedFile(root, newPath, files[oldPath]);
            var updatedFiles = manifest.Files.Select(file => file == source ? file with { Path = newPath } : file)
                .OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray();
            var updated = manifest with
            {
                FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Files = updatedFiles,
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(updatedFiles.Select(file => file.Path), Directories(manifest))
            };
            var updatedContent = files.Where(pair => pair.Key != oldPath).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            updatedContent.Add(newPath, files[oldPath]);
            try { ValidateAndWrite(root, updated, updatedContent); }
            catch { TryDelete(StudioProjectSourceDirectory.ResolvePath(root, newPath)); throw; }
            TryDelete(StudioProjectSourceDirectory.ResolvePath(root, oldPath));
            return updated;
        }
    }

    public static StudioProjectManifest DeleteFile(string root, string path, string expectedManifestHash)
    {
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            if (!manifest.Files.Any(file => file.Path == path)) throw new IOException("The selected project file no longer exists.");
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Files = manifest.Files.Where(file => file.Path != path).ToImmutableArray() };
            var updatedFiles = files.Where(pair => pair.Key != path).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            updated = updated with { Directories = StudioProjectSourceArchive.DirectoriesForPaths(
                updated.Files.Select(file => file.Path), Directories(manifest)) };
            ValidateAndWrite(root, updated, updatedFiles);
            TryDelete(StudioProjectSourceDirectory.ResolvePath(root, path));
            return updated;
        }
    }

    public static StudioProjectFile CreateDocumentation(string root, string relativePath, string markdown, string expectedManifestHash)
    {
        var bytes = Utf8Markdown(markdown);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceArchive.ValidateProjectPath(relativePath);
            if (!relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Project documentation files must use the .md extension.", nameof(relativePath));
            if (manifest.Files.Any(file => string.Equals(file.Path, relativePath, StringComparison.OrdinalIgnoreCase)) ||
                Directories(manifest).Any(directory => string.Equals(directory, relativePath, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("A project file or folder already uses that path.");
            var document = new StudioProjectFile("documentation", "doc-" + Guid.NewGuid().ToString("N"), "1",
                relativePath, StudioProjectSourceArchive.ComputeContentHash(bytes));
            var updatedFiles = manifest.Files.Append(document).OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray();
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion, Files = updatedFiles,
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(updatedFiles.Select(file => file.Path), Directories(manifest)) };
            var allFiles = files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            allFiles.Add(relativePath, bytes);
            _ = StudioProjectSourceArchive.CreateArchive(updated, allFiles);
            StudioProjectSourceDirectory.WriteIndexedFile(root, relativePath, bytes);
            try { StudioProjectSourceDirectory.ReplaceManifest(root, updated); }
            catch { TryDelete(StudioProjectSourceDirectory.ResolvePath(root, relativePath)); throw; }
            return document;
        }
    }

    public static StudioProjectFile SaveDocumentation(string root, string relativePath, string markdown, string expectedManifestHash)
    {
        var bytes = Utf8Markdown(markdown);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            var original = manifest.Files.SingleOrDefault(file => file.Kind == "documentation" && file.Path == relativePath)
                ?? throw new IOException("The selected Markdown file no longer exists.");
            var updatedDocument = original with { Sha256 = StudioProjectSourceArchive.ComputeContentHash(bytes) };
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion,
                Files = manifest.Files.Select(file => file == original ? updatedDocument : file).ToImmutableArray(),
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(manifest.Files.Select(file => file.Path), Directories(manifest)) };
            var updatedFiles = files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            updatedFiles[relativePath] = bytes;
            _ = StudioProjectSourceArchive.CreateArchive(updated, updatedFiles);
            StudioProjectSourceDirectory.ReplaceIndexedFile(root, relativePath, bytes);
            try { StudioProjectSourceDirectory.ReplaceManifest(root, updated); }
            catch
            {
                StudioProjectSourceDirectory.ReplaceIndexedFile(root, relativePath, files[relativePath]);
                throw;
            }
            return updatedDocument;
        }
    }

    public static StudioProjectFile DuplicateFile(string root, string path, string expectedManifestHash)
        => DuplicateFile(root, path, expectedManifestHash, null);

    public static StudioProjectFile DuplicateFile(string root, string path, string expectedManifestHash, string? targetDirectory)
    {
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, files) = ReadExpected(root, expectedManifestHash);
            var source = manifest.Files.SingleOrDefault(file => file.Path == path)
                ?? throw new IOException("The selected project file no longer exists.");
            var newId = StudioProjectSourceArchive.SafeAssetFileStem(source.Id[..Math.Min(source.Id.Length, 48)]) + "-copy-" + Guid.NewGuid().ToString("N")[..8];
            var directory = targetDirectory ?? Path.GetDirectoryName(source.Path)?.Replace('\\', '/') ?? "";
            var version = source.Version;
            byte[] bytes;
            switch (source.Kind)
            {
                case "workflow":
                {
                    var original = StudioProjectSourceArchive.DeserializeWorkflowSource(files[path]);
                    var definition = original.Definition with { WorkflowId = newId,
                    DisplayName = CopyName(original.Definition.DisplayName ?? "Workflow", 128) };
                    bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeWorkflowSource(definition, original.Layout));
                    break;
                }
                case "skill":
                {
                    var original = StudioProjectSourceArchive.DeserializeSkillAsset(files[path]);
                    var skill = WorkflowSkill.Create(newId, version, CopyName(original.Skill.Name, 128), original.Skill.Instructions);
                    bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(original with { Skill = skill }));
                    break;
                }
                case "agent-profile":
                {
                    var original = StudioProjectSourceArchive.DeserializeAgentProfileAsset(files[path]);
                    var profile = original.Profile;
                    var duplicate = AgentProfile.Create(newId, version, CopyName(profile.Name, 128), profile.Description,
                        profile.ProviderId, profile.ProviderVersion, profile.Instructions, profile.Tools, profile.Skills,
                        profile.ReasoningEffort, profile.MaxOutputTokens, profile.Channels);
                    bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeAgentProfileAsset(original with { Profile = duplicate }));
                    break;
                }
                case "graph-function":
                {
                    var original = StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(files[path]);
                    var function = original.Item.Function.Definition with { FunctionId = newId,
                        DisplayName = CopyName(original.Item.Function.Definition.DisplayName, 160) };
                    var item = new GraphFunctionCatalogEntry(new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function)), original.Item.Folder);
                    bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeGraphFunctionAsset(item, original.Layout));
                    break;
                }
                case "documentation":
                    bytes = files[path];
                    break;
                default: throw new InvalidDataException("The selected project asset kind cannot be duplicated.");
            }

            var hash = StudioProjectSourceArchive.ComputeContentHash(bytes);
            var extension = Path.GetExtension(source.Path);
            if (extension == ".json") extension = source.Kind switch
            { "workflow" => ".workflow", "skill" => ".skill", "agent-profile" => ".agent", _ => ".function" };
            var destinationStem = source.Kind == "documentation"
                ? StudioProjectSourceArchive.SafeAssetFileStem(Path.GetFileNameWithoutExtension(source.Path) + " copy")
                : StudioProjectSourceArchive.SafeAssetFileStem(newId);
            var relativePath = StudioProjectSourceArchive.AssetPathInDirectory(directory, destinationStem, extension, hash);
            var duplicateFile = new StudioProjectFile(source.Kind, newId, version, relativePath, hash);
            var updatedFiles = manifest.Files.Append(duplicateFile).OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray();
            var updated = manifest with { FormatVersion = StudioProjectSourceArchive.FormatVersion, Files = updatedFiles,
                Directories = StudioProjectSourceArchive.DirectoriesForPaths(updatedFiles.Select(file => file.Path), Directories(manifest)) };
            var allFiles = files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            allFiles.Add(relativePath, bytes);
            _ = StudioProjectSourceArchive.CreateArchive(updated, allFiles);
            StudioProjectSourceDirectory.WriteIndexedFile(root, relativePath, bytes);
            try { StudioProjectSourceDirectory.ReplaceManifest(root, updated); }
            catch { TryDelete(StudioProjectSourceDirectory.ResolvePath(root, relativePath)); throw; }
            return duplicateFile;
        }
    }

    public static StudioProjectManifest CopyDirectory(string root, string sourcePath, string targetParent, string expectedManifestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var gate = StudioFolderSourceWriter.LockFor(root);
        lock (gate)
        {
            var (manifest, _) = ReadExpected(root, expectedManifestHash);
            StudioProjectSourceDirectory.RejectPathLinks(root, StudioProjectSourceDirectory.ResolvePath(root, sourcePath));
            if (!Directories(manifest).Contains(sourcePath, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The selected project folder no longer exists.");
            var name = Path.GetFileName(sourcePath);
            var parent = targetParent.TrimEnd('/');
            var copyName = name + " copy";
            var candidate = string.IsNullOrEmpty(parent) ? copyName : $"{parent}/{copyName}";
            var suffix = 2;
            while (Directories(manifest).Any(directory => string.Equals(directory, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   manifest.Files.Any(file => string.Equals(file.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                copyName = $"{name} copy {suffix++}";
                candidate = string.IsNullOrEmpty(parent) ? copyName : $"{parent}/{copyName}";
            }

            var sourceFiles = manifest.Files.Where(file => file.Path.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var currentHash = expectedManifestHash;
            try
            {
                foreach (var file in sourceFiles)
                {
                    var remainder = file.Path[(sourcePath.Length + 1)..];
                    var fileParent = Path.GetDirectoryName(remainder)?.Replace('\\', '/') ?? "";
                    var target = string.IsNullOrEmpty(fileParent) ? candidate : $"{candidate}/{fileParent}";
                    _ = DuplicateFile(root, file.Path, currentHash, target);
                    currentHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(root).Manifest);
                }

                var current = StudioProjectSourceDirectory.Read(root).Manifest;
                var copiedDirectories = Directories(manifest).Where(directory => directory.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
                    .Select(directory => candidate + directory[sourcePath.Length..]);
                var withEmptyFolders = current with
                {
                    FormatVersion = StudioProjectSourceArchive.FormatVersion,
                    Directories = StudioProjectSourceArchive.DirectoriesForPaths([], Directories(current).Concat(copiedDirectories).Append(candidate))
                };
                var currentFiles = StudioProjectSourceDirectory.Read(root).Files;
                ValidateAndWrite(root, withEmptyFolders, currentFiles);
                foreach (var directory in Directories(withEmptyFolders).Where(directory => directory.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    directory.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)))
                    StudioProjectSourceDirectory.EnsureSafeDirectories(root, StudioProjectSourceDirectory.ResolvePath(root, directory));
                return withEmptyFolders;
            }
            catch
            {
                // A failed copy must not leave a partial subtree in the project manifest.
                var current = StudioProjectSourceDirectory.Read(root);
                foreach (var duplicate in current.Manifest.Files.Where(file => file.Path.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    var latestHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(root).Manifest);
                    _ = DeleteFile(root, duplicate.Path, latestHash);
                }
                var afterFiles = StudioProjectSourceDirectory.Read(root);
                var directories = Directories(afterFiles.Manifest).Where(directory => !directory.Equals(candidate, StringComparison.OrdinalIgnoreCase) &&
                    !directory.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
                var rollback = afterFiles.Manifest with { Directories = directories };
                ValidateAndWrite(root, rollback, afterFiles.Files);
                throw;
            }
        }
    }

    private static IEnumerable<string> Directories(StudioProjectManifest manifest) =>
        StudioProjectSourceArchive.DirectoriesForPaths(manifest.Files.Select(file => file.Path),
            manifest.Directories.IsDefault ? [] : manifest.Directories);

    private static (StudioProjectManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) ReadExpected(string root, string expectedHash)
    {
        var snapshot = StudioProjectSourceDirectory.Read(root);
        if (StudioProjectSourceArchive.ComputeManifestHash(snapshot.Manifest) != expectedHash)
            throw new IOException("The project changed outside Studio. Reload before continuing.");
        return snapshot;
    }

    private static void ValidateAndWrite(string root, StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        _ = StudioProjectSourceArchive.CreateArchive(manifest, files);
        StudioProjectSourceDirectory.ReplaceManifest(root, manifest);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static string CopyName(string value, int maximumLength)
    {
        const string suffix = " copy";
        var baseLength = Math.Max(1, maximumLength - suffix.Length);
        return value[..Math.Min(value.Length, baseLength)].TrimEnd() + suffix;
    }

    private static byte[] Utf8Markdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        byte[] bytes;
        try { bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(markdown); }
        catch (EncoderFallbackException exception) { throw new ArgumentException("Markdown must contain valid Unicode text.", nameof(markdown), exception); }
        if (bytes.Length > StudioProjectSourceArchive.MaximumFileBytes)
            throw new ArgumentException("Markdown file exceeds the supported 3 MiB size.", nameof(markdown));
        return bytes;
    }
}
