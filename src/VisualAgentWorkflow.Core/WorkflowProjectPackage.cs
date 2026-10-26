using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowProjectPackageEntry(string WorkflowId, string Path, string Sha256,
    string DefinitionHash, string LayoutHash);

/// <summary>
/// Release manifest for selected executable workflow entry points. Requirements describe host dependencies;
/// they do not configure endpoints, install code, or authorize capabilities.
/// </summary>
public sealed record WorkflowProjectPackageManifest(string FormatVersion, string ProjectId, string ProjectName,
    ImmutableArray<WorkflowProjectPackageEntry> Workflows, ImmutableArray<PackageRequirement> Requirements);

/// <summary>A fully validated, immutable view of a consumer workflow bundle.</summary>
public sealed record WorkflowProjectPackage(WorkflowProjectPackageManifest Manifest,
    ImmutableSortedDictionary<string, WorkflowPackage> Workflows, string ArtifactHash);

/// <summary>
/// Deterministic bounded ZIP packaging for consumer releases. This is deliberately separate from editable
/// Studio source archives. Checksums establish integrity only; they are not signatures or permissions.
/// </summary>
public static class WorkflowProjectPackageJson
{
    public const string FileExtension = ".vawpkg";
    public const string ManifestPath = "vaw.release.json";
    public const string FormatVersion = "1.0";
    public const int MaximumWorkflows = 128;
    public const int MaximumArchiveBytes = 32 * 1024 * 1024;
    private const int MaximumManifestBytes = 3 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    /// <summary>Builds a release from an explicit set of validated workflow packages.</summary>
    public static byte[] CreateArchive(string projectId, string projectName, IEnumerable<WorkflowPackage> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ValidateProjectIdentity(projectId, projectName);

        var selected = workflows.Select(ValidateWorkflowPackage)
            .OrderBy(package => package.Definition.WorkflowId, StringComparer.Ordinal).ToArray();
        if (selected.Length is < 1 or > MaximumWorkflows)
            throw new InvalidDataException($"Select between 1 and {MaximumWorkflows} workflows for a release.");
        if (selected.Select(package => package.Definition.WorkflowId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidDataException("A release cannot contain duplicate workflow IDs.");

        var packageFiles = selected.ToDictionary(
            package => PackagePath(package.Definition.WorkflowId),
            package => Encoding.UTF8.GetBytes(WorkflowPackageJson.Serialize(package.Definition, package.Layout)),
            StringComparer.Ordinal);
        var entries = selected.Select(package =>
        {
            var path = PackagePath(package.Definition.WorkflowId);
            var bytes = packageFiles[path];
            return new WorkflowProjectPackageEntry(package.Definition.WorkflowId, path, Hash(bytes),
                package.DefinitionHash, package.LayoutHash);
        }).ToImmutableArray();
        var requirements = SortRequirements(selected.SelectMany(package => package.Requirements).Distinct());
        var manifest = new WorkflowProjectPackageManifest(FormatVersion, projectId, projectName, entries, requirements);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("Workflow release manifest exceeds the supported size.");

        var allFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        allFiles.Add(ManifestPath, manifestBytes);
        foreach (var (path, bytes) in packageFiles) allFiles.Add(path, bytes);
        if (allFiles.Count > MaximumWorkflows + 1 || allFiles.Values.Sum(bytes => (long)bytes.Length) > MaximumArchiveBytes)
            throw new InvalidDataException("Workflow release exceeds the supported size.");

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in allFiles)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var content = entry.Open();
                content.Write(bytes);
            }
        }
        if (stream.Length > MaximumArchiveBytes) throw new InvalidDataException("Workflow release exceeds the supported archive size.");
        return stream.ToArray();
    }

    /// <summary>Validates all files and derived requirements in memory; it never extracts archive paths.</summary>
    public static WorkflowProjectPackage Read(ReadOnlyMemory<byte> archiveBytes)
    {
        if (archiveBytes.Length is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("Workflow release archive size is outside the supported limit.");

        using var stream = new MemoryStream(archiveBytes.ToArray(), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 2 or > MaximumWorkflows + 1)
            throw new InvalidDataException("Workflow release has an unsupported number of files.");

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (!uniquePaths.Add(entry.FullName))
                throw new InvalidDataException("Workflow release contains duplicate or case-colliding paths.");
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
                throw new InvalidDataException("Workflow release must not contain symbolic links.");
            if (entry.Length is < 0 or > WorkflowPackageJson.MaximumBytes)
                throw new InvalidDataException("A workflow package exceeds the supported size.");
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumArchiveBytes)
                throw new InvalidDataException("Workflow release contents exceed the supported size.");
            using var input = entry.Open();
            var bytes = ReadBounded(input, (int)entry.Length);
            if (!files.TryAdd(entry.FullName, bytes))
                throw new InvalidDataException("Workflow release contains duplicate paths.");
        }

        if (!files.Remove(ManifestPath, out var manifestBytes))
            throw new InvalidDataException("Workflow release manifest is missing.");
        var manifest = DeserializeManifest(manifestBytes);
        ValidateEntries(manifest, files);

        var packages = ImmutableSortedDictionary.CreateBuilder<string, WorkflowPackage>(StringComparer.Ordinal);
        foreach (var entry in manifest.Workflows)
        {
            var package = WorkflowPackageJson.Deserialize(StrictUtf8(files[entry.Path]));
            if (package.Definition.WorkflowId != entry.WorkflowId || package.DefinitionHash != entry.DefinitionHash ||
                package.LayoutHash != entry.LayoutHash)
                throw new InvalidDataException($"Workflow package '{entry.WorkflowId}' does not match its release manifest.");
            packages.Add(entry.WorkflowId, package);
        }

        var derivedRequirements = SortRequirements(packages.Values.SelectMany(package => package.Requirements).Distinct());
        if (!manifest.Requirements.SequenceEqual(derivedRequirements))
            throw new InvalidDataException("Workflow release requirements do not match the selected workflow packages.");
        return new(manifest, packages.ToImmutable(), Hash(archiveBytes.Span));
    }

    public static string ComputeHash(ReadOnlySpan<byte> archiveBytes) => Hash(archiveBytes);

    private static WorkflowPackage ValidateWorkflowPackage(WorkflowPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(package.Definition, package.Layout));
    }

    private static WorkflowProjectPackageManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("Workflow release manifest size is outside the supported limit.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<WorkflowProjectPackageManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Workflow release manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(WorkflowProjectPackageManifest manifest)
    {
        ValidateProjectIdentity(manifest.ProjectId, manifest.ProjectName);
        if (manifest.FormatVersion != FormatVersion || manifest.Workflows.IsDefaultOrEmpty ||
            manifest.Workflows.Length > MaximumWorkflows || manifest.Requirements.IsDefault ||
            manifest.Requirements.Length > 16_384 || manifest.Workflows.Any(entry => entry is null))
            throw new InvalidDataException("Workflow release manifest is invalid or uses an unsupported format version.");

        var workflowIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Workflows)
        {
            if (!ValidIdentifier(entry.WorkflowId) || entry.Path != PackagePath(entry.WorkflowId) ||
                !IsHash(entry.Sha256) || !IsHash(entry.DefinitionHash) || !IsHash(entry.LayoutHash) ||
                !workflowIds.Add(entry.WorkflowId) || !paths.Add(entry.Path))
                throw new InvalidDataException("Workflow release manifest contains an invalid or duplicate workflow entry.");
        }
        var sortedIds = manifest.Workflows.Select(entry => entry.WorkflowId).Order(StringComparer.Ordinal);
        if (!manifest.Workflows.Select(entry => entry.WorkflowId).SequenceEqual(sortedIds, StringComparer.Ordinal))
            throw new InvalidDataException("Workflow release manifest entries must be sorted by workflow ID.");

        var requirementSet = new HashSet<PackageRequirement>();
        foreach (var requirement in manifest.Requirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.Kind) || requirement.Kind.Length > 64 ||
                requirement.Kind.Any(char.IsControl) || string.IsNullOrWhiteSpace(requirement.Id) || requirement.Id.Length > 256 ||
                requirement.Id.Any(char.IsControl) || requirement.Version is null || requirement.Version.Length > 128 ||
                requirement.Version.Any(char.IsControl) || requirement.ContractHash is not null && !IsHash(requirement.ContractHash) ||
                requirement.DescriptionHash is not null && !IsHash(requirement.DescriptionHash) || !requirementSet.Add(requirement))
                throw new InvalidDataException("Workflow release manifest contains an invalid or duplicate requirement.");
        }
        if (!manifest.Requirements.SequenceEqual(SortRequirements(manifest.Requirements)))
            throw new InvalidDataException("Workflow release requirements must be sorted.");
    }

    private static void ValidateEntries(WorkflowProjectPackageManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count != manifest.Workflows.Length)
            throw new InvalidDataException("Workflow release contains files missing from or extra to its manifest.");
        foreach (var entry in manifest.Workflows)
            if (!files.TryGetValue(entry.Path, out var bytes) || Hash(bytes) != entry.Sha256)
                throw new InvalidDataException($"Workflow package '{entry.WorkflowId}' is missing or has a content hash mismatch.");
    }

    private static ImmutableArray<PackageRequirement> SortRequirements(IEnumerable<PackageRequirement> requirements) => requirements
        .OrderBy(requirement => requirement.Kind, StringComparer.Ordinal)
        .ThenBy(requirement => requirement.Id, StringComparer.Ordinal)
        .ThenBy(requirement => requirement.Version, StringComparer.Ordinal)
        .ThenBy(requirement => requirement.ContractHash, StringComparer.Ordinal)
        .ThenBy(requirement => requirement.DescriptionHash, StringComparer.Ordinal)
        .ToImmutableArray();

    private static void ValidateProjectIdentity(string projectId, string projectName)
    {
        if (!ValidIdentifier(projectId) || string.IsNullOrWhiteSpace(projectName) || projectName.Length > 80 ||
            projectName.Any(char.IsControl))
            throw new InvalidDataException("Workflow release project identity is invalid.");
    }

    private static bool ValidIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        !value.Any(char.IsControl) && value.Trim() == value;

    private static string PackagePath(string workflowId)
    {
        if (!ValidIdentifier(workflowId)) throw new InvalidDataException("Workflow IDs in a release must be non-empty and contain no control characters.");
        var idHash = Hash(Encoding.UTF8.GetBytes(workflowId));
        return $"workflows/{idHash}.json";
    }

    private static void ValidateArchivePath(string path)
    {
        if (path.Length is 0 or > 256 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") || path.EndsWith('/'))
            throw new InvalidDataException("Workflow release contains an unsafe archive path.");
        if (path != ManifestPath && !(path.StartsWith("workflows/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) &&
                                     path.Length == "workflows/".Length + 64 + ".json".Length))
            throw new InvalidDataException("Workflow release contains an unexpected file path.");
    }

    private static byte[] ReadBounded(Stream input, int expectedLength)
    {
        var buffer = new byte[expectedLength + 1];
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = input.Read(buffer, readTotal, buffer.Length - readTotal);
            if (read == 0) break;
            readTotal += read;
        }
        if (readTotal != expectedLength) throw new InvalidDataException("Workflow release entry length does not match its ZIP metadata.");
        return buffer.AsSpan(0, readTotal).ToArray();
    }

    private static string StrictUtf8(ReadOnlySpan<byte> bytes) => new UTF8Encoding(false, true).GetString(bytes);
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
