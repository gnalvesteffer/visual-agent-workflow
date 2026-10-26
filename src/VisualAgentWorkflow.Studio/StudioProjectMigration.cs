using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Hosting;

namespace VisualAgentWorkflow.Studio;

/// <summary>Single-host lock and offline catalog-to-folder cutover. Never invoke while Studio is serving requests.</summary>
public static class StudioProjectMigration
{
    private const string LockFileName = ".studio.lock";
    private const string JournalFileName = "project-cutover.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Journal(string ProjectId, string Destination, string StagingDirectory, string ManifestHash);

    /// <summary>Acquires the exclusive host lock used by both the web host and offline maintenance commands.</summary>
    public static IDisposable AcquireHostLock(string studioDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studioDataDirectory);
        var root = Path.GetFullPath(studioDataDirectory);
        Directory.CreateDirectory(root);
        try
        {
            return new FileStream(Path.Combine(root, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another Visual Agent Workflow Studio process is using this data directory. Stop it before running maintenance or starting a second Studio.", exception);
        }
    }

    /// <summary>Completes an interrupted cutover before any project workspace is exposed to the web host.</summary>
    public static void Recover(string studioDataDirectory, StudioProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var journalPath = Path.Combine(Path.GetFullPath(studioDataDirectory), JournalFileName);
        if (!File.Exists(journalPath)) return;
        if (new FileInfo(journalPath).Length > 64 * 1024)
            throw new InvalidDataException("The project cutover journal exceeds the supported size.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath), JsonOptions)
            ?? throw new InvalidDataException("The project cutover journal is empty.");
        ValidateJournal(journal);
        var project = store.GetActive(journal.ProjectId);

        if (project.IsFolderBacked)
        {
            if (!PathEquals(project.FolderPath!, journal.Destination))
                throw new InvalidDataException("The cutover journal conflicts with the project's current folder mapping.");
            VerifyFolder(journal.Destination, journal.ManifestHash);
            File.Delete(journalPath);
            return;
        }

        if (Directory.Exists(journal.Destination))
            VerifyFolder(journal.Destination, journal.ManifestHash);
        else if (Directory.Exists(journal.StagingDirectory))
        {
            VerifyFolder(journal.StagingDirectory, journal.ManifestHash);
            Directory.Move(journal.StagingDirectory, journal.Destination);
            VerifyFolder(journal.Destination, journal.ManifestHash);
        }
        else
        {
            // Before publication, the catalog is still authoritative; abort the incomplete operation.
            File.Delete(journalPath);
            return;
        }

        store.SetFolderSource(journal.ProjectId, journal.Destination);
        File.Delete(journalPath);
    }

    /// <summary>
    /// Migrates the current editable source snapshot after acquiring the same exclusive lock as the web host.
    /// Run history and catalog revision history are retained locally.
    /// </summary>
    public static async Task<StudioProject> MigrateToFolderAsync(string studioDataDirectory, string projectId,
        string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studioDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var dataDirectory = Path.GetFullPath(studioDataDirectory);
        using var hostLock = AcquireHostLock(dataDirectory);
        var store = new StudioProjectStore(dataDirectory);
        Recover(dataDirectory, store);
        var project = store.GetActive(projectId);
        if (project.IsFolderBacked) throw new InvalidOperationException("The selected project is already folder-backed.");

        var destinationPath = Path.GetFullPath(destination);
        if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
            throw new IOException("Choose a destination folder that does not already exist.");
        store.EnsureFolderPathAvailable(projectId, destinationPath);
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destination));
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination parent folder does not exist.");

        var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(dataDirectory, "runs")),
            NullLogger<LocalWorkflowRuntime>.Instance);
        var workspace = new StudioProjectWorkspace(dataDirectory, store, runtime);
        workspace.Select(projectId);
        var archive = await StudioProjectSourceArchive.CreateAsync(workspace, cancellationToken).ConfigureAwait(false);
        var (manifest, files) = StudioProjectSourceArchive.Read(archive);
        var expectedHash = StudioProjectSourceArchive.ComputeManifestHash(manifest);
        var stagingPath = Path.Combine(parent, ".vaw-cutover-" + Guid.NewGuid().ToString("N"));

        StudioProjectSourceDirectory.Write(stagingPath, manifest, files);
        VerifyFolder(stagingPath, expectedHash);
        var journal = new Journal(projectId, destinationPath, stagingPath, expectedHash);
        var journalPath = Path.Combine(dataDirectory, JournalFileName);
        WriteJournal(journalPath, journal);

        // The exclusive process lock makes this offline commit safe from live circuits and another Studio host.
        Directory.Move(stagingPath, destinationPath);
        VerifyFolder(destinationPath, expectedHash);
        var migrated = store.SetFolderSource(projectId, destinationPath);
        File.Delete(journalPath);
        return migrated;
    }

    private static void VerifyFolder(string path, string expectedManifestHash)
    {
        var (manifest, _) = StudioProjectSourceDirectory.Read(path);
        if (!string.Equals(StudioProjectSourceArchive.ComputeManifestHash(manifest), expectedManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("The staged project folder does not match the captured source manifest.");
    }

    private static void WriteJournal(string path, Journal journal)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void ValidateJournal(Journal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.ProjectId) || string.IsNullOrWhiteSpace(journal.Destination) ||
            string.IsNullOrWhiteSpace(journal.StagingDirectory) || journal.ManifestHash is null ||
            !Path.IsPathFullyQualified(journal.Destination) ||
            !Path.IsPathFullyQualified(journal.StagingDirectory) || journal.ManifestHash.Length != 64 ||
            !journal.ManifestHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("The project cutover journal contains invalid fields.");
        var destinationParent = Path.GetDirectoryName(journal.Destination);
        var stagingParent = Path.GetDirectoryName(journal.StagingDirectory);
        if (destinationParent is null || stagingParent is null || !PathEquals(destinationParent, stagingParent) ||
            !Path.GetFileName(journal.StagingDirectory).StartsWith(".vaw-cutover-", StringComparison.Ordinal))
            throw new InvalidDataException("The project cutover journal paths are inconsistent.");
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
