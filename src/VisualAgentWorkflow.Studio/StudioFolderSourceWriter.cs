using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

internal static class StudioFolderSourceWriter
{
    private const string PendingDirectory = ".vaw";
    private const string PendingFileName = "pending-workflow-save.json";
    private const int MaximumPendingBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions PendingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private static readonly ConcurrentDictionary<string, object> RootLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static object LockFor(string root) => RootLocks.GetOrAdd(Path.GetFullPath(root), static _ => new object());

    public static void RecoverPendingWorkflowSave(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var journalPath = Path.Combine(fullRoot, PendingDirectory, PendingFileName);
        if (!File.Exists(journalPath)) return;
        StudioProjectSourceDirectory.RejectPathLinks(fullRoot, journalPath);
        var journalBytes = StudioProjectSourceDirectory.ReadBounded(journalPath, MaximumPendingBytes,
            "The pending workflow save journal exceeds the supported size.");
        var pending = JsonSerializer.Deserialize<PendingWorkflowSave>(journalBytes, PendingJsonOptions)
            ?? throw new InvalidDataException("The pending workflow save journal is empty.");
        if (pending.FormatVersion != "1.0" || string.IsNullOrWhiteSpace(pending.TargetPath) || string.IsNullOrWhiteSpace(pending.TempPath) ||
            pending.ExpectedManifestHash.Length != 64 || pending.NewManifestHash.Length != 64 || pending.ContentHash.Length != 64)
            throw new InvalidDataException("The pending workflow save journal is invalid.");

        var currentManifestPath = Path.Combine(fullRoot, StudioProjectSourceArchive.ManifestFileName);
        StudioProjectSourceDirectory.RejectPathLinks(fullRoot, currentManifestPath);
        var currentManifest = StudioProjectSourceArchive.DeserializeManifest(
            StudioProjectSourceDirectory.ReadBounded(currentManifestPath, StudioProjectSourceArchive.MaximumFileBytes,
                "The project manifest exceeds the supported size."));
        var currentManifestHash = StudioProjectSourceArchive.ComputeManifestHash(currentManifest);
        var newManifestBytes = Encoding.UTF8.GetBytes(pending.NewManifestJson);
        var newManifest = StudioProjectSourceArchive.DeserializeManifest(newManifestBytes);
        if (!string.Equals(StudioProjectSourceArchive.ComputeManifestHash(newManifest), pending.NewManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("The pending workflow save journal has a mismatched manifest hash.");

        var target = StudioProjectSourceDirectory.ResolvePath(fullRoot, pending.TargetPath);
        var temporary = StudioProjectSourceDirectory.ResolvePath(fullRoot, pending.TempPath);
        if (File.Exists(target)) StudioProjectSourceDirectory.RejectPathLinks(fullRoot, target);
        if (File.Exists(temporary)) StudioProjectSourceDirectory.RejectPathLinks(fullRoot, temporary);
        if (string.Equals(currentManifestHash, pending.NewManifestHash, StringComparison.Ordinal))
        {
            var currentTarget = StudioProjectSourceDirectory.ReadBounded(target, StudioProjectSourceArchive.MaximumFileBytes,
                "The committed workflow source is missing or exceeds the supported size.");
            if (!string.Equals(StudioProjectSourceArchive.ComputeContentHash(currentTarget), pending.ContentHash, StringComparison.Ordinal))
                throw new IOException("The project manifest claims a pending workflow save completed, but the workflow source does not match it.");
            DeleteIfPresent(temporary);
            DeleteIfPresent(journalPath);
            return;
        }
        if (!string.Equals(currentManifestHash, pending.ExpectedManifestHash, StringComparison.Ordinal))
            throw new IOException("The project changed while a workflow save was pending. Reload the folder and resolve the conflict.");

        var temporaryBytes = StudioProjectSourceDirectory.ReadBounded(temporary, StudioProjectSourceArchive.MaximumFileBytes,
            "The pending workflow source is missing or exceeds the supported size.");
        if (!string.Equals(StudioProjectSourceArchive.ComputeContentHash(temporaryBytes), pending.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("The pending workflow source hash does not match its journal.");
        if (File.Exists(target))
        {
            var existing = StudioProjectSourceDirectory.ReadBounded(target, StudioProjectSourceArchive.MaximumFileBytes,
                "A project source file exceeds the supported size.");
            var existingHash = StudioProjectSourceArchive.ComputeContentHash(existing);
            if (existingHash != pending.ContentHash && existingHash != pending.ExpectedTargetHash)
                throw new IOException("The workflow source changed while a save was pending. Reload the folder and resolve the conflict.");
        }
        ReplaceAtomically(target, temporaryBytes);
        ReplaceAtomically(currentManifestPath, newManifestBytes);
        DeleteIfPresent(temporary);
        DeleteIfPresent(journalPath);
    }

    public static void PublishMutableWorkflow(string root, string expectedManifestHash, StudioProjectManifest updatedManifest,
        string relativePath, byte[] content, string contentHash, string? expectedTargetHash,
        Func<string, IOException> conflict)
    {
        var fullRoot = Path.GetFullPath(root);
        var currentManifest = StudioProjectSourceDirectory.Read(fullRoot).Manifest;
        var currentHash = StudioProjectSourceArchive.ComputeManifestHash(currentManifest);
        if (currentHash != expectedManifestHash) throw conflict("project manifest");
        var target = StudioProjectSourceDirectory.ResolvePath(fullRoot, relativePath);
        StudioProjectSourceDirectory.EnsureSafeDirectories(fullRoot, Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            StudioProjectSourceDirectory.RejectPathLinks(fullRoot, target);
            var current = StudioProjectSourceDirectory.ReadBounded(target, StudioProjectSourceArchive.MaximumFileBytes,
                "A project source file exceeds the supported size.");
            if (expectedTargetHash is null || StudioProjectSourceArchive.ComputeContentHash(current) != expectedTargetHash)
                throw conflict(relativePath);
        }
        var newManifestJson = StudioProjectSourceArchive.SerializeManifest(updatedManifest);
        var newManifestHash = StudioProjectSourceArchive.ComputeManifestHash(updatedManifest);
        var pendingDirectory = Path.Combine(fullRoot, PendingDirectory);
        StudioProjectSourceDirectory.EnsureSafeDirectories(fullRoot, pendingDirectory);
        var temporaryPath = $"{PendingDirectory}/workflow-{Guid.NewGuid():N}.tmp";
        var temporary = StudioProjectSourceDirectory.ResolvePath(fullRoot, temporaryPath);
        WriteDurably(temporary, content);
        var journal = new PendingWorkflowSave("1.0", expectedManifestHash, expectedTargetHash, contentHash,
            newManifestHash, relativePath, temporaryPath, newManifestJson);
        WriteDurably(Path.Combine(fullRoot, PendingDirectory, PendingFileName),
            JsonSerializer.SerializeToUtf8Bytes(journal, PendingJsonOptions));
        try
        {
            RecoverPendingWorkflowSave(fullRoot);
        }
        catch
        {
            // Recovery intentionally leaves the journal in place when the external state diverged.
            throw;
        }
    }

    public static void PublishImmutableAsset(string root, string expectedManifestHash,
        StudioProjectManifest updatedManifest, string relativePath, byte[] content, string contentHash,
        Func<string, IOException> conflict)
    {
        var fullRoot = Path.GetFullPath(root);
        var destination = StudioProjectSourceDirectory.ResolvePath(fullRoot, relativePath);
        WriteImmutableAsset(fullRoot, destination, content, contentHash);

        if (StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(fullRoot).Manifest) != expectedManifestHash)
            throw conflict("project manifest");

        var writtenContent = StudioProjectSourceDirectory.ReadBounded(destination,
            StudioProjectSourceArchive.MaximumFileBytes, "A project source file exceeds the supported size.");
        if (StudioProjectSourceArchive.ComputeContentHash(writtenContent) != contentHash)
            throw conflict(relativePath);

        var manifestPath = Path.Combine(fullRoot, StudioProjectSourceArchive.ManifestFileName);
        ReplaceAtomically(manifestPath, Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeManifest(updatedManifest)));
    }

    private static void WriteImmutableAsset(string root, string destination, byte[] content, string expectedHash)
    {
        var parent = Path.GetDirectoryName(destination)!;
        StudioProjectSourceDirectory.EnsureSafeDirectories(root, parent);
        if (File.Exists(destination))
        {
            StudioProjectSourceDirectory.RejectPathLinks(root, destination);
            var existingContent = StudioProjectSourceDirectory.ReadBounded(destination,
                StudioProjectSourceArchive.MaximumFileBytes, "A project source file exceeds the supported size.");
            if (StudioProjectSourceArchive.ComputeContentHash(existingContent) != expectedHash)
                throw new InvalidDataException("An immutable project source path contains different content.");
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

    private static void ReplaceAtomically(string destination, byte[] content)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteDurably(string destination, byte[] content)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record PendingWorkflowSave(string FormatVersion, string ExpectedManifestHash,
        string? ExpectedTargetHash, string ContentHash, string NewManifestHash, string TargetPath,
        string TempPath, string NewManifestJson);
}
