using System.Text;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioProjectSnapshot(string Id, DateTimeOffset CreatedAtUtc, string ManifestHash,
    long ArchiveBytes, bool IsValid, string? Problem);
public sealed record StudioProjectSnapshotPreview(string Id, string CurrentManifestHash, string SnapshotManifestHash,
    int AddedFiles, int RemovedFiles, int ChangedFiles);
public sealed record StudioProjectSourceSnapshot(string ArchiveContentHash, StudioProjectManifest Manifest,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Stores validated, local-only project restore points under the ignored project sidecar.</summary>
public sealed class StudioProjectSnapshotStore
{
    public const int MaximumSnapshots = 20;
    public const long MaximumStoredBytes = 256L * 1024 * 1024;
    private const string SnapshotDirectoryName = "snapshots";
    private const string SnapshotExtension = ".vawsrc.zip";
    private const int MaximumSnapshotBytes = StudioProjectSourceArchive.MaximumArchiveBytes + 1024 * 1024;

    /// <summary>Loads one validated, content-addressed restore point fully into memory for isolated inspection or execution.</summary>
    public StudioProjectSourceSnapshot ReadSourceSnapshot(string projectDirectory, string snapshotContentHash)
    {
        ValidateSnapshotId(snapshotContentHash);
        var root = Path.GetFullPath(projectDirectory);
        var (manifest, files) = ReadSnapshot(root, snapshotContentHash);
        return new(snapshotContentHash, manifest, files);
    }
    public IReadOnlyList<StudioProjectSnapshot> List(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var snapshotsPath = ExistingSnapshotsPath(root);
        if (snapshotsPath is null) return [];

        return Directory.EnumerateFiles(snapshotsPath, "*" + SnapshotExtension, SearchOption.TopDirectoryOnly)
            .Select(path => Inspect(root, snapshotsPath, path))
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .ThenBy(snapshot => snapshot.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public StudioProjectSnapshot Create(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        lock (StudioFolderSourceWriter.LockFor(root))
        {
            var (manifest, files) = StudioProjectSourceDirectory.Read(root);
            var archive = StudioProjectSourceArchive.CreateArchive(manifest, files);
            // Keep the same validation contract used by source import/export before persisting local state.
            var (validatedManifest, _) = StudioProjectSourceArchive.Read(archive);
            var id = StudioProjectSourceArchive.ComputeContentHash(archive);
            var existingSnapshotsPath = ExistingSnapshotsPath(root);
            var existingSnapshotPath = existingSnapshotsPath is null
                ? null
                : Path.Combine(existingSnapshotsPath, id + SnapshotExtension);
            if (existingSnapshotPath is not null && File.Exists(existingSnapshotPath))
            {
                StudioProjectSourceDirectory.RejectPathLinks(root, existingSnapshotPath);
                var existing = Inspect(root, existingSnapshotsPath!, existingSnapshotPath);
                if (!existing.IsValid || existing.Id != id)
                    throw new InvalidDataException("A restore point already exists at this content hash but failed validation.");
                EnsureIgnored(root);
                return existing;
            }

            var existingFiles = existingSnapshotsPath is null
                ? []
                : Directory.EnumerateFiles(existingSnapshotsPath, "*" + SnapshotExtension, SearchOption.TopDirectoryOnly).ToArray();
            foreach (var existingFile in existingFiles) StudioProjectSourceDirectory.RejectPathLinks(root, existingFile);
            if (existingFiles.Length >= MaximumSnapshots)
                throw new InvalidOperationException($"The local restore-point limit is {MaximumSnapshots}. Delete an older restore point before creating another.");
            var storedBytes = existingFiles.Sum(file => new FileInfo(file).Length);
            if (storedBytes + archive.Length > MaximumStoredBytes)
                throw new InvalidOperationException("The local restore-point storage limit is 256 MiB. Delete older restore points before creating another.");

            EnsureIgnored(root);
            var snapshotsPath = EnsureSnapshotsPath(root);
            var path = Path.Combine(snapshotsPath, id + SnapshotExtension);
            if (File.Exists(path))
            {
                StudioProjectSourceDirectory.RejectPathLinks(root, path);
                var existing = Inspect(root, snapshotsPath, path);
                if (!existing.IsValid || existing.Id != id)
                    throw new InvalidDataException("A restore point already exists at this content hash but failed validation.");
                return existing;
            }

            WriteNewAtomically(root, path, archive);
            return Inspect(root, snapshotsPath, path) with
            {
                ManifestHash = StudioProjectSourceArchive.ComputeManifestHash(validatedManifest)
            };
        }
    }

    public StudioProjectSnapshot Restore(string projectDirectory, string snapshotId, string expectedCurrentManifestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentManifestHash);
        ValidateSnapshotId(snapshotId);
        var root = Path.GetFullPath(projectDirectory);
        lock (StudioFolderSourceWriter.LockFor(root))
        {
            var (currentManifest, _) = StudioProjectSourceDirectory.Read(root);
            var currentHash = StudioProjectSourceArchive.ComputeManifestHash(currentManifest);
            if (!string.Equals(currentHash, expectedCurrentManifestHash, StringComparison.Ordinal))
                throw new IOException("The project changed after the restore preview. Reload the folder and preview again.");

            var (snapshotManifest, snapshotFiles) = ReadSnapshot(root, snapshotId);
            if (!string.Equals(currentManifest.ProjectId, snapshotManifest.ProjectId, StringComparison.Ordinal))
                throw new InvalidDataException("This restore point belongs to a different project.");

            foreach (var asset in snapshotManifest.Files)
            {
                var destination = StudioProjectSourceDirectory.ResolvePath(root, asset.Path);
                RejectLinkIfPresent(root, destination);
                if (File.Exists(destination))
                {
                    var currentBytes = StudioProjectSourceDirectory.ReadBounded(destination,
                        StudioProjectSourceArchive.MaximumFileBytes, "A project source file exceeds the supported size.");
                    if (StudioProjectSourceArchive.ComputeContentHash(currentBytes) != asset.Sha256)
                        throw new InvalidDataException($"Cannot restore because '{asset.Path}' already exists with different content.");
                    continue;
                }

                var bytes = snapshotFiles[asset.Path];
                StudioProjectSourceDirectory.EnsureSafeDirectories(root, Path.GetDirectoryName(destination)!);
                WriteNewAtomically(root, destination, bytes);
            }

            // Assets are immutable, content-addressed files. The single manifest replacement is the
            // authority switch, so a crash before it leaves only harmless unindexed files behind.
            var (latestManifest, _) = StudioProjectSourceDirectory.Read(root);
            if (StudioProjectSourceArchive.ComputeManifestHash(latestManifest) != expectedCurrentManifestHash)
                throw new IOException("The project changed during restore. Its manifest was left unchanged; reload and preview again.");

            ReplaceManifestAtomically(root, snapshotManifest);
            var restoredPath = SnapshotPath(root, snapshotId);
            return Inspect(root, Path.GetDirectoryName(restoredPath)!, restoredPath);
        }
    }

    public StudioProjectSnapshotPreview Preview(string projectDirectory, string snapshotId)
    {
        ValidateSnapshotId(snapshotId);
        var root = Path.GetFullPath(projectDirectory);
        var (currentManifest, _) = StudioProjectSourceDirectory.Read(root);
        var (snapshotManifest, _) = ReadSnapshot(root, snapshotId);
        if (currentManifest.ProjectId != snapshotManifest.ProjectId)
            throw new InvalidDataException("This restore point belongs to a different project.");

        var current = currentManifest.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        var snapshot = snapshotManifest.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        var added = snapshot.Keys.Except(current.Keys, StringComparer.Ordinal).Count();
        var removed = current.Keys.Except(snapshot.Keys, StringComparer.Ordinal).Count();
        var changed = current.Keys.Intersect(snapshot.Keys, StringComparer.Ordinal)
            .Count(path => current[path] != snapshot[path]);
        return new(snapshotId, StudioProjectSourceArchive.ComputeManifestHash(currentManifest),
            StudioProjectSourceArchive.ComputeManifestHash(snapshotManifest), added, removed, changed);
    }

    public void Delete(string projectDirectory, string snapshotId)
    {
        ValidateSnapshotId(snapshotId);
        var root = Path.GetFullPath(projectDirectory);
        lock (StudioFolderSourceWriter.LockFor(root))
        {
            var snapshotsPath = ExistingSnapshotsPath(root);
            if (snapshotsPath is null) return;
            var path = Path.Combine(snapshotsPath, snapshotId + SnapshotExtension);
            if (!File.Exists(path)) return;
            StudioProjectSourceDirectory.RejectPathLinks(root, path);
            File.Delete(path);
        }
    }

    private static (StudioProjectManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) ReadSnapshot(string root, string id)
    {
        var snapshotsPath = ExistingSnapshotsPath(root)
            ?? throw new FileNotFoundException("The local restore-point folder is missing.");
        var path = Path.Combine(snapshotsPath, id + SnapshotExtension);
        StudioProjectSourceDirectory.RejectPathLinks(root, path);
        var bytes = StudioProjectSourceDirectory.ReadBounded(path, MaximumSnapshotBytes,
            "The local restore point exceeds the supported size.");
        if (StudioProjectSourceArchive.ComputeContentHash(bytes) != id)
            throw new InvalidDataException("The local restore point failed its content-hash check.");
        return StudioProjectSourceArchive.Read(bytes);
    }

    private static StudioProjectSnapshot Inspect(string root, string snapshotsPath, string path)
    {
        var fileName = Path.GetFileName(path);
        var id = fileName[..^SnapshotExtension.Length];
        var created = DateTime.UnixEpoch;
        try
        {
            created = File.GetLastWriteTimeUtc(path);
            ValidateSnapshotId(id);
            StudioProjectSourceDirectory.RejectPathLinks(root, path);
            var bytes = StudioProjectSourceDirectory.ReadBounded(path, MaximumSnapshotBytes,
                "The local restore point exceeds the supported size.");
            if (StudioProjectSourceArchive.ComputeContentHash(bytes) != id)
                throw new InvalidDataException("Content hash does not match the file name.");
            var (manifest, _) = StudioProjectSourceArchive.Read(bytes);
            return new(id, created, StudioProjectSourceArchive.ComputeManifestHash(manifest), bytes.Length, true, null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            long length = 0;
            try { length = new FileInfo(path).Length; } catch (IOException) { }
            return new(id, created, "", length, false, exception.Message);
        }
    }

    private static string? ExistingSnapshotsPath(string root)
    {
        var sidecar = Path.Combine(root, ".vaw");
        if (!Directory.Exists(sidecar))
        {
            if (File.Exists(sidecar)) throw new InvalidDataException("The project .vaw sidecar path is not a directory.");
            return null;
        }
        StudioProjectSourceDirectory.RejectPathLinks(root, sidecar);
        var snapshots = Path.Combine(sidecar, SnapshotDirectoryName);
        if (!Directory.Exists(snapshots))
        {
            if (File.Exists(snapshots)) throw new InvalidDataException("The .vaw snapshots path is not a directory.");
            return null;
        }
        StudioProjectSourceDirectory.RejectPathLinks(root, snapshots);
        return snapshots;
    }

    private static string EnsureSnapshotsPath(string root)
    {
        var sidecar = Path.Combine(root, ".vaw");
        if (Directory.Exists(sidecar)) StudioProjectSourceDirectory.RejectPathLinks(root, sidecar);
        else if (File.Exists(sidecar)) throw new InvalidDataException("The project .vaw sidecar path is not a directory.");
        else Directory.CreateDirectory(sidecar);
        var snapshots = Path.Combine(sidecar, SnapshotDirectoryName);
        if (Directory.Exists(snapshots)) StudioProjectSourceDirectory.RejectPathLinks(root, snapshots);
        else if (File.Exists(snapshots)) throw new InvalidDataException("The .vaw snapshots path is not a directory.");
        else Directory.CreateDirectory(snapshots);
        return snapshots;
    }

    private static string SnapshotPath(string root, string id) =>
        Path.Combine(ExistingSnapshotsPath(root) ?? throw new FileNotFoundException("The local restore-point folder is missing."), id + SnapshotExtension);

    private static void EnsureIgnored(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        RejectLinkIfPresent(root, path);
        var current = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (current.Length > 1024 * 1024) throw new InvalidDataException("The project .gitignore exceeds the supported size.");
        var text = new UTF8Encoding(false, true).GetString(current);
        if (text.Split('\n').Any(line => line.TrimEnd('\r').Trim() is ".vaw/" or "/.vaw/" or ".vaw" or "/.vaw")) return;
        var separator = current.Length == 0 || current[^1] == (byte)'\n' ? "" : "\n";
        var suffix = Encoding.UTF8.GetBytes(separator + "/.vaw/\n");
        var updated = new byte[current.Length + suffix.Length];
        current.CopyTo(updated, 0);
        suffix.CopyTo(updated, current.Length);
        var existed = File.Exists(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(updated);
            if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(current))
                throw new IOException("The project .gitignore changed while preparing local restore points. Try again.");
            File.Move(temporary, path, overwrite: existed);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ReplaceManifestAtomically(string root, StudioProjectManifest manifest)
    {
        var destination = Path.Combine(root, StudioProjectSourceArchive.ManifestFileName);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(manifest));
        try
        {
            StudioProjectSourceDirectory.RejectPathLinks(root, destination);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteNewAtomically(string root, string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            RejectLinkIfPresent(root, destination);
            if (File.Exists(destination)) throw new IOException("An unexpected file already exists at an immutable source path.");
            StudioProjectSourceDirectory.RejectPathLinks(root, Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinkIfPresent(string root, string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (Directory.Exists(parent)) StudioProjectSourceDirectory.RejectPathLinks(root, parent);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Project restore points must not follow symbolic links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void ValidateSnapshotId(string id)
    {
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)) || id.Any(char.IsUpper))
            throw new ArgumentException("A lower-case SHA-256 restore-point ID is required.", nameof(id));
    }
}
