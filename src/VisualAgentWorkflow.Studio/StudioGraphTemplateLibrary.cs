using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Catalog-backed authoring store for immutable pure template revisions.</summary>
/// <remarks>Folder-backed projects use the manifest-indexed source model and are intentionally not wired here yet.</remarks>
public sealed class StudioGraphTemplateLibrary : IGraphTemplateCatalog
{
    private const string CurrentCatalogFormatVersion = "1.0";
    private const int MaximumEntries = 256;
    private const int MaximumCatalogBytes = 16 * 1_048_576;
    private readonly object gate = new();
    private readonly string path;
    private List<StudioGraphTemplateAsset> entries;

    public StudioGraphTemplateLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "graph-templates.json");
        entries = File.Exists(path) ? LoadEntries(File.ReadAllBytes(path)) : [];
        ValidateEntries(entries);
    }

    public IReadOnlyList<GraphTemplateCatalogEntry> List()
    {
        lock (gate) return OrderedEntries().Select(entry => entry.ToCatalogEntry()).ToArray();
    }

    public GraphTemplateCatalogEntry? Get(string templateId, int version)
    {
        lock (gate)
        {
            return entries.FirstOrDefault(entry => entry.Document.Definition.TemplateId == templateId &&
                entry.Document.Definition.Version == version)?.ToCatalogEntry();
        }
    }

    public void Add(GraphTemplateDefinition template, string folder)
    {
        ValidateTemplate(template, folder);
        var document = CreateDocument(template);
        lock (gate)
        {
            if (entries.Any(entry => SameRevision(entry.Document.Definition, template)))
                throw new ArgumentException("This graph template revision already exists. Create a new version for edits.");
            Save([.. entries, new(folder, document)]);
        }
    }

    public bool Move(string templateId, int version, string folder)
    {
        ValidateFolder(folder);
        lock (gate)
        {
            var index = entries.FindIndex(entry => entry.Document.Definition.TemplateId == templateId &&
                entry.Document.Definition.Version == version);
            if (index < 0) return false;
            var updated = entries.ToList();
            updated[index] = updated[index] with { Folder = folder };
            Save(updated);
            return true;
        }
    }

    private IEnumerable<StudioGraphTemplateAsset> OrderedEntries() => entries
        .OrderBy(entry => entry.Folder, StringComparer.Ordinal)
        .ThenBy(entry => entry.Document.Definition.DisplayName, StringComparer.Ordinal)
        .ThenBy(entry => entry.Document.Definition.Version);

    private void Save(List<StudioGraphTemplateAsset> updated)
    {
        ValidateEntries(updated);
        var document = new StudioGraphTemplateCatalogDocument(CurrentCatalogFormatVersion,
            updated.OrderBy(entry => entry.Folder, StringComparer.Ordinal)
                .ThenBy(entry => entry.Document.Definition.TemplateId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Document.Definition.Version)
                .ToImmutableArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaximumCatalogBytes)
            throw new InvalidDataException("The graph template catalog exceeds the supported 16 MiB size.");

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            entries = updated;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<StudioGraphTemplateAsset> LoadEntries(byte[] bytes)
    {
        if (bytes.Length > MaximumCatalogBytes)
            throw new InvalidDataException("The graph template catalog exceeds the supported 16 MiB size.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var catalog = JsonSerializer.Deserialize<StudioGraphTemplateCatalogDocument>(bytes, Options)
            ?? throw new InvalidDataException("The graph template catalog is empty.");
        if (catalog.FormatVersion != CurrentCatalogFormatVersion || catalog.Entries.IsDefault)
            throw new InvalidDataException("The graph template catalog is incomplete or uses an unsupported format version.");
        var entries = catalog.Entries.ToList();
        ValidateEntries(entries);
        return entries;
    }

    private static void ValidateEntries(IReadOnlyList<StudioGraphTemplateAsset> assets)
    {
        if (assets.Count > MaximumEntries)
            throw new InvalidDataException("The graph template catalog contains too many entries.");
        var identities = new HashSet<(string Id, int Version)>();
        foreach (var asset in assets)
        {
            if (asset is null || asset.Document is null || asset.Document.Definition is null ||
                asset.Document.FormatVersion != GraphTemplateLimits.CurrentFormatVersion ||
                !identities.Add((asset.Document.Definition.TemplateId, asset.Document.Definition.Version)))
                throw new InvalidDataException("The graph template catalog contains an invalid or duplicate revision.");
            ValidateFolder(asset.Folder);
            var issues = GraphTemplateJson.Validate(asset.Document.Definition);
            if (!issues.IsEmpty || !string.Equals(asset.Document.ContentHash,
                    GraphTemplateJson.ComputeHash(asset.Document.Definition), StringComparison.Ordinal))
                throw new InvalidDataException("The graph template catalog contains an invalid or modified revision.");
        }
    }

    private static GraphTemplateDocument CreateDocument(GraphTemplateDefinition template) =>
        new(GraphTemplateLimits.CurrentFormatVersion, template, GraphTemplateJson.ComputeHash(template));

    private static void ValidateTemplate(GraphTemplateDefinition template, string folder)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = GraphTemplateJson.Validate(template);
        if (!issues.IsEmpty) throw new ArgumentException(string.Join(" ", issues.Select(issue => issue.Message)), nameof(template));
        ValidateFolder(folder);
    }

    private static bool SameRevision(GraphTemplateDefinition left, GraphTemplateDefinition right) =>
        left.TemplateId == right.TemplateId && left.Version == right.Version;

    private static void ValidateFolder(string? folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Length > 256 || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 8 ||
            folder.Split('/').Any(part => part is "." or ".." || part.Length > 64 || part.Contains('\\')))
            throw new ArgumentException("Use a logical folder path with at most eight short segments.", nameof(folder));
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 64
    };

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The graph template catalog contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

public sealed record StudioGraphTemplateAsset(string Folder, GraphTemplateDocument Document)
{
    public GraphTemplateCatalogEntry ToCatalogEntry() =>
        new(new(Document.Definition, Document.ContentHash), Folder);
}

public sealed record StudioGraphTemplateCatalogDocument(string FormatVersion, ImmutableArray<StudioGraphTemplateAsset> Entries);
