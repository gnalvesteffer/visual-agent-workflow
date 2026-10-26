using System.Collections.Concurrent;
using System.Text;

namespace VisualAgentWorkflow.Studio;

internal static class StudioFolderSourceWriter
{
    private static readonly ConcurrentDictionary<string, object> RootLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static object LockFor(string root) => RootLocks.GetOrAdd(Path.GetFullPath(root), static _ => new object());

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
}
