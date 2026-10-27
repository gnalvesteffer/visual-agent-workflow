using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioProjectFile(string Kind, string Id, string Version, string Path, string Sha256);
public sealed record StudioProjectManifest(string FormatVersion, string ProjectId, string ProjectName,
    ImmutableArray<StudioProjectFile> Files);
public sealed record StudioWorkflowSource(string FormatVersion, WorkflowDefinition Definition, WorkflowLayout Layout);

/// <summary>Deterministic, credential-free source snapshot export. Not a consumer runtime installer.</summary>
public static class StudioProjectSourceArchive
{
    public const string ManifestFileName = "vaw.project.json";
    public const string FormatVersion = "1.0";
    public const string WorkflowSourceFormatVersion = "1.0";
    public const int MaximumFiles = 2_000;
    public const int MaximumFileBytes = 3 * 1024 * 1024;
    public const int MaximumArchiveBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    public static async Task<byte[]> CreateAsync(StudioProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var sourceFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var assetEntries = new List<StudioProjectFile>();

        foreach (var draft in await workspace.Catalog.ListDraftsAsync().WaitAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layout = workspace.Catalog is IWorkflowDesignCatalog designs
                ? (await designs.GetDesignAsync(draft.Definition.WorkflowId).WaitAsync(cancellationToken))?.Layout
                : null;
            var json = SerializeWorkflowSource(draft.Definition, layout ?? WorkflowLayout.Create(draft.Definition));
            Add(sourceFiles, assetEntries, "workflow", draft.Definition.WorkflowId, "draft",
                WorkflowDraftPath(draft.Definition.WorkflowId, Hash(System.Text.Encoding.UTF8.GetBytes(json))), json);
        }

        foreach (var item in workspace.Skills.List())
        {
            var json = SerializeSkillAsset(item);
            var contentHash = ComputeContentHash(System.Text.Encoding.UTF8.GetBytes(json));
            Add(sourceFiles, assetEntries, "skill", item.Skill.Id, item.Skill.Version,
                SkillAssetPath(item.Skill.Id, item.Skill.Version, contentHash), json);
        }
        foreach (var item in workspace.AgentProfiles.List())
            Add(sourceFiles, assetEntries, "agent-profile", item.Profile.Id, item.Profile.Version,
                AssetPath("agents", item.Profile.Id, item.Profile.Version), JsonSerializer.Serialize(item, JsonOptions));
        foreach (var asset in workspace.Functions.ListAssets())
        {
            var item = asset.Item;
            var version = item.Function.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Add(sourceFiles, assetEntries, "graph-function", item.Function.Definition.FunctionId, version,
                AssetPath("functions", item.Function.Definition.FunctionId, version), SerializeGraphFunctionAsset(item, asset.Layout));
        }

        var manifest = new StudioProjectManifest(FormatVersion, workspace.CurrentProjectId, workspace.CurrentProjectName,
            assetEntries.OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray());
        if (assetEntries.Count > MaximumFiles) throw new InvalidDataException("Project contains too many assets.");
        return CreateArchive(manifest, sourceFiles);
    }

    public static byte[] CreateArchive(StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        ValidateManifest(manifest);
        ValidateFiles(manifest, files);
        if (files.Count > MaximumFiles || files.Values.Any(content => content.Length > MaximumFileBytes))
            throw new InvalidDataException("Project source exceeds the supported file limits.");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaximumFileBytes || files.Values.Sum(content => (long)content.Length) + manifestBytes.Length > MaximumArchiveBytes)
            throw new InvalidDataException("Project source exceeds the supported size.");
        var sortedFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in files) sortedFiles.Add(file.Key, file.Value);
        return Zip(manifestBytes, sortedFiles);
    }

    /// <summary>Validates archive paths, bounds, manifest membership, and content hashes without extracting files.</summary>
    public static (StudioProjectManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) Read(ReadOnlyMemory<byte> archive)
    {
        if (archive.Length is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("Project archive size is outside the supported limit.");
        using var stream = new MemoryStream(archive.ToArray(), writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 1 or > MaximumFiles + 1) throw new InvalidDataException("Project archive has an unsupported number of files.");

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var entry in zip.Entries)
        {
            ValidatePath(entry.FullName);
            if (entry.FullName.EndsWith('/')) throw new InvalidDataException("Project archive must not contain directory entries.");
            if ((entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
                throw new InvalidDataException("Project archive must not contain symbolic links.");
            if (!uniquePaths.Add(entry.FullName)) throw new InvalidDataException("Project archive contains duplicate or case-colliding paths.");
            if (entry.Length is < 0 or > MaximumFileBytes) throw new InvalidDataException("A project file exceeds the supported size.");
            totalBytes = checked(totalBytes + (int)entry.Length);
            if (totalBytes > MaximumArchiveBytes) throw new InvalidDataException("Project contents exceed the supported size.");
            using var entryStream = entry.Open();
            var content = ReadBounded(entryStream, (int)entry.Length);
            if (!files.TryAdd(entry.FullName, content)) throw new InvalidDataException("Project archive contains duplicate paths.");
        }

        if (!files.Remove(ManifestFileName, out var manifestBytes)) throw new InvalidDataException("Project manifest is missing.");
        var manifest = DeserializeManifest(manifestBytes);
        ValidateFiles(manifest, files);
        return (manifest, files);
    }

    /// <summary>Imports a validated source snapshot as a new local project. Run history and host settings are not imported.</summary>
    public static async Task<StudioProject> ImportAsNewProjectAsync(ReadOnlyMemory<byte> archive,
        StudioProjectStore store, StudioProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspace);

        // Parse and validate the complete archive before creating any project state.
        var (manifest, files) = Read(archive);
        var workflows = new List<StudioWorkflowSource>();
        var skills = new List<SkillLibraryItem>();
        var profiles = new List<AgentProfileLibraryItem>();
        var functions = new List<StudioGraphFunctionAsset>();
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = files[file.Path];
            switch (file.Kind)
            {
                case "workflow": workflows.Add(DeserializeWorkflowSource(content)); break;
                case "skill": skills.Add(JsonSerializer.Deserialize<SkillLibraryItem>(content, JsonOptions)!); break;
                case "agent-profile": profiles.Add(JsonSerializer.Deserialize<AgentProfileLibraryItem>(content, JsonOptions)!); break;
                case "graph-function": functions.Add(DeserializeGraphFunctionSourceAsset(content)); break;
            }
        }

        var project = store.Create(manifest.ProjectName);
        var previousProjectId = workspace.CurrentProjectId;
        try
        {
            workspace.Select(project.Id);
            foreach (var source in workflows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (workspace.Catalog is not IWorkflowDesignCatalog designs)
                    throw new InvalidOperationException("The selected workflow catalog does not support workflow layouts.");
                await designs.SaveDesignAsync(source.Definition, source.Layout, 0).WaitAsync(cancellationToken);
            }
            foreach (var item in skills)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspace.Skills.Add(item.Skill.Id, item.Skill.Version, item.Skill.Name, item.Skill.Instructions, item.Folder);
            }
            foreach (var item in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspace.AgentProfiles.Add(item.Profile, item.Folder);
            }
            foreach (var item in functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspace.Functions.Add(item.Item.Function.Definition, item.Item.Folder, item.Layout);
            }
            return project;
        }
        catch
        {
            // A new imported project must never become the selected workspace after a failed import.
            store.SetArchived(project.Id, true);
            workspace.Select(previousProjectId);
            throw;
        }
    }

    public static string SerializeManifest(StudioProjectManifest manifest)
    {
        ValidateManifest(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    public static string WorkflowDraftPath(string workflowId, string contentHash)
    {
        if (!ValidAssetId(workflowId) || contentHash is null || contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A safe workflow ID and SHA-256 content hash are required.");
        var safeId = Segment(workflowId);
        var idHash = Hash(System.Text.Encoding.UTF8.GetBytes(workflowId))[..8];
        return $"workflows/{safeId}-{idHash}-{contentHash[..16].ToLowerInvariant()}.workflow";
    }

    public static string SkillAssetPath(string id, string version, string contentHash)
    {
        if (contentHash is null || contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 content hash is required.", nameof(contentHash));
        var basePath = AssetPath("skills", id, version);
        return basePath[..basePath.LastIndexOf('.')] + "-" + contentHash.ToLowerInvariant() + ".skill";
    }

    public static string VersionedAssetPath(string directory, string id, string version, string contentHash)
    {
        if (directory is not ("agents" or "functions"))
            throw new ArgumentException("A supported project asset directory is required.", nameof(directory));
        if (contentHash is null || contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 content hash is required.", nameof(contentHash));
        var basePath = AssetPath(directory, id, version);
        var extension = directory switch { "agents" => ".agent", "functions" => ".function", _ => throw new ArgumentException("A supported project asset directory is required.", nameof(directory)) };
        return basePath[..basePath.LastIndexOf('.')] + "-" + contentHash.ToLowerInvariant() + extension;
    }

    public static string SerializeSkillAsset(SkillLibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return JsonSerializer.Serialize(item, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static SkillLibraryItem DeserializeSkillAsset(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Project skill size is outside the supported limit.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<SkillLibraryItem>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Project skill is empty.");
    }

    public static string SerializeAgentProfileAsset(AgentProfileLibraryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return JsonSerializer.Serialize(item, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static AgentProfileLibraryItem DeserializeAgentProfileAsset(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Project agent profile size is outside the supported limit.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<AgentProfileLibraryItem>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Project agent profile is empty.");
    }

    public static string SerializeGraphFunctionAsset(GraphFunctionCatalogEntry item, WorkflowLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var asset = StudioGraphFunctionAsset.Create(item, layout);
        return JsonSerializer.Serialize(asset, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static GraphFunctionCatalogEntry DeserializeGraphFunctionAsset(ReadOnlySpan<byte> bytes)
        => DeserializeGraphFunctionSourceAsset(bytes).Item;

    public static StudioGraphFunctionAsset DeserializeGraphFunctionSourceAsset(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Project graph function size is outside the supported limit.");
        return StudioGraphFunctionAsset.Deserialize(bytes, JsonOptions);
    }

    public static string ComputeManifestHash(StudioProjectManifest manifest) =>
        ComputeContentHash(System.Text.Encoding.UTF8.GetBytes(SerializeManifest(manifest)));

    public static string ComputeContentHash(ReadOnlySpan<byte> content) => Hash(content.ToArray());

    public static StudioProjectManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Project manifest size is outside the supported limit.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<StudioProjectManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Project manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static string SerializeWorkflowSource(WorkflowDefinition definition, WorkflowLayout layout)
    {
        var normalizedDefinition = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(definition));
        var normalizedLayout = WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(layout, normalizedDefinition), normalizedDefinition);
        return JsonSerializer.Serialize(new StudioWorkflowSource(WorkflowSourceFormatVersion, normalizedDefinition, normalizedLayout), JsonOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static StudioWorkflowSource DeserializeWorkflowSource(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Workflow source size is outside the supported limit.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var source = JsonSerializer.Deserialize<StudioWorkflowSource>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Workflow source is empty.");
        if (source.FormatVersion != WorkflowSourceFormatVersion || source.Definition is null || source.Layout is null)
            throw new InvalidDataException("Workflow source is incomplete or uses an unsupported format version.");
        var definition = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(source.Definition));
        var layout = WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(source.Layout, definition), definition);
        return new(source.FormatVersion, definition, layout);
    }

    private static void ValidateManifest(StudioProjectManifest manifest)
    {
        if (manifest.FormatVersion != FormatVersion || !ValidProjectId(manifest.ProjectId) ||
            string.IsNullOrWhiteSpace(manifest.ProjectName) || manifest.ProjectName.Length > 80 ||
            manifest.ProjectName.Any(char.IsControl) || manifest.Files.IsDefault || manifest.Files.Length > MaximumFiles)
            throw new InvalidDataException("Project manifest is invalid or uses an unsupported format version.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null || !ValidAssetId(file.Id) || string.IsNullOrWhiteSpace(file.Version) || file.Version.Length > 64 ||
                file.Version.Any(char.IsControl) || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                file.Kind is not ("workflow" or "skill" or "agent-profile" or "graph-function"))
                throw new InvalidDataException("Project manifest contains an invalid asset reference.");
            ValidatePath(file.Path);
            var prefix = file.Kind switch
            {
                "workflow" => "workflows/",
                "skill" => "skills/",
                "agent-profile" => "agents/",
                _ => "functions/"
            };
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal) || !paths.Add(file.Path) ||
                !assetIds.Add($"{file.Kind}\0{file.Id}\0{file.Version}"))
                throw new InvalidDataException("Project manifest contains duplicate or misplaced assets.");
        }
    }

    private static void ValidateFiles(StudioProjectManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count != manifest.Files.Length) throw new InvalidDataException("Project archive contains files missing from or extra to its manifest.");
        foreach (var file in manifest.Files)
        {
            if (!files.TryGetValue(file.Path, out var content) || !string.Equals(Hash(content), file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Project asset '{file.Path}' is missing or has a content hash mismatch.");
            if (file.Kind == "workflow")
            {
                var source = DeserializeWorkflowSource(content);
                if (!string.Equals(source.Definition.WorkflowId, file.Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"Project asset '{file.Path}' does not match its workflow ID.");
            }
            else ValidateLibraryAsset(file, content);
        }
    }

    private static void ValidateLibraryAsset(StudioProjectFile file, byte[] content)
    {
        switch (file.Kind)
        {
            case "skill":
            {
                var item = DeserializeSkillAsset(content);
                if (item?.Skill is not { } skill || skill != WorkflowSkill.Create(skill.Id, skill.Version, skill.Name, skill.Instructions) ||
                    skill.Id != file.Id || skill.Version != file.Version || !ValidFolder(item.Folder))
                    throw new InvalidDataException($"Project skill '{file.Path}' is invalid or does not match its manifest entry.");
                break;
            }
            case "agent-profile":
            {
                var item = DeserializeAgentProfileAsset(content);
                if (item?.Profile is not { } profile || profile.Id != file.Id || profile.Version != file.Version ||
                    !ValidFolder(item.Folder) || profile.ContentHash != AgentProfile.Create(profile.Id, profile.Version,
                        profile.Name, profile.Description, profile.ProviderId, profile.ProviderVersion, profile.Instructions,
                        profile.Tools, profile.Skills, profile.ReasoningEffort, profile.MaxOutputTokens, profile.Channels).ContentHash ||
                    !IsValidAgentProfile(profile, item.Folder))
                    throw new InvalidDataException($"Project agent profile '{file.Path}' is invalid or does not match its manifest entry.");
                break;
            }
            case "graph-function":
            {
                var asset = DeserializeGraphFunctionSourceAsset(content);
                var item = asset.Item;
                if (item?.Function is not { } function ||
                    !int.TryParse(file.Version, System.Globalization.CultureInfo.InvariantCulture, out var version) ||
                    function.Definition.FunctionId != file.Id || function.Definition.Version != version ||
                    !ValidFolder(item.Folder) || !GraphFunctionJson.Validate(function.Definition).IsEmpty ||
                    function.ContentHash != GraphFunctionJson.ComputeHash(function.Definition))
                    throw new InvalidDataException($"Project graph function '{file.Path}' is invalid or does not match its manifest entry.");
                break;
            }
        }
    }

    private static bool IsValidAgentProfile(AgentProfile profile, string folder)
    {
        try
        {
            StudioAgentProfileLibrary.Validate(profile, folder);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidFolder(string? folder) => folder is not null && folder.Length <= 256 &&
        folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= 8 &&
        folder.Split('/').All(part => part is not "." and not ".." && part.Length <= 64 && !part.Contains('\\'));

    private static byte[] Zip(byte[] manifest, SortedDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, ManifestFileName, manifest);
            foreach (var file in files) AddEntry(archive, file.Key, file.Value);
        }
        if (stream.Length > MaximumArchiveBytes) throw new InvalidDataException("Project archive exceeds the supported size.");
        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UnixEpoch.AddYears(10); // ZIP's earliest supported timestamp: 1980-01-01.
        using var destination = entry.Open();
        destination.Write(content);
    }

    private static void Add(IDictionary<string, byte[]> files, ICollection<StudioProjectFile> entries,
        string kind, string id, string version, string path, string json)
    {
        var content = System.Text.Encoding.UTF8.GetBytes(json);
        if (content.Length > MaximumFileBytes) throw new InvalidDataException($"Project asset '{path}' exceeds the supported size.");
        if (!files.TryAdd(path, content)) throw new InvalidDataException("Project assets produced a duplicate source path.");
        entries.Add(new(kind, id, version, path, Hash(content)));
    }

    private static string AssetPath(string directory, string id, string version)
    {
        var safeId = Segment(id);
        var safeVersion = Segment(version);
        var extension = directory switch { "skills" => ".skill", "agents" => ".agent", "functions" => ".function", _ => ".json" };
        var file = directory == "workflows" ? $"{safeId}-{Hash(System.Text.Encoding.UTF8.GetBytes(id))[..8]}{extension}" :
            $"{safeId}/{safeVersion}-{Hash(System.Text.Encoding.UTF8.GetBytes(version))[..8]}{extension}";
        return $"{directory}/{file}";
    }

    private static string Segment(string value)
    {
        var safe = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
        return safe.Length is > 0 and <= 64 && safe is not "." and not ".." ? safe : "asset";
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.StartsWith('/') || path.Contains('\\') ||
            path.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains(':') || segment.Any(char.IsControl)))
            throw new InvalidDataException("Project asset path must be a safe relative path within the project.");
    }

    private static byte[] ReadBounded(Stream stream, int declaredLength)
    {
        using var output = new MemoryStream(declaredLength);
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > declaredLength || output.Length + read > MaximumFileBytes)
                throw new InvalidDataException("A project file exceeds its declared size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != declaredLength) throw new InvalidDataException("A project file was truncated.");
        return output.ToArray();
    }

    private static bool ValidProjectId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 80 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ValidAssetId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Any(char.IsControl);

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Project manifest contains a duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
        }
    }
}
