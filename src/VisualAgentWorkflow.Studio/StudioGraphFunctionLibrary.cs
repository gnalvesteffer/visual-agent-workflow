using System.Text.Json;
using System.Text.Json.Serialization;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Local authoring catalog for immutable pure graph-function revisions; workflows embed exact snapshots.</summary>
public sealed class StudioGraphFunctionLibrary : IGraphFunctionCatalog
{
    private readonly object gate = new();
    private readonly string path;
    private List<StudioGraphFunctionAsset> entries;

    public StudioGraphFunctionLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "graph-functions.json");
        entries = File.Exists(path) ? LoadEntries(File.ReadAllText(path)) : [];
        if (entries.Any(entry => entry is null || !IsIntact(entry) || !IsValidFolder(entry.Item.Folder)) ||
            entries.GroupBy(entry => (entry.Item.Function.Definition.FunctionId, entry.Item.Function.Definition.Version)).Any(group => group.Count() > 1))
            throw new InvalidDataException("The graph function library contains an invalid or modified revision.");
    }

    public IReadOnlyList<GraphFunctionCatalogEntry> List()
    {
        lock (gate) return OrderedEntries().Select(entry => entry.Item).ToArray();
    }

    public IReadOnlyList<StudioGraphFunctionAsset> ListAssets()
    {
        lock (gate) return OrderedEntries().ToArray();
    }

    public void Add(GraphFunctionDefinition function, string folder, WorkflowLayout? layout = null)
    {
        Validate(function, folder);
        var configuration = new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function));
        lock (gate)
        {
            if (entries.Any(entry => entry.Item.Function.Definition.FunctionId == function.FunctionId &&
                entry.Item.Function.Definition.Version == function.Version))
                throw new ArgumentException("This graph function version already exists. Create a new version for edits.");
            Save([.. entries, StudioGraphFunctionAsset.Create(new(configuration, folder), layout)]);
        }
    }

    public bool Move(string functionId, int version, string folder)
    {
        ValidateFolder(folder);
        lock (gate)
        {
            var index = entries.FindIndex(entry => entry.Item.Function.Definition.FunctionId == functionId &&
                entry.Item.Function.Definition.Version == version);
            if (index < 0) return false;
            var updated = entries.ToList();
            updated[index] = updated[index] with { Item = updated[index].Item with { Folder = folder } };
            Save(updated);
            return true;
        }
    }

    public bool Delete(string functionId, int version)
    {
        lock (gate)
        {
            var updated = entries.Where(entry => entry.Item.Function.Definition.FunctionId != functionId ||
                entry.Item.Function.Definition.Version != version).ToList();
            if (updated.Count == entries.Count) return false;
            Save(updated);
            return true;
        }
    }

    private IEnumerable<StudioGraphFunctionAsset> OrderedEntries() => entries.OrderBy(entry => entry.Item.Folder, StringComparer.Ordinal)
        .ThenBy(entry => entry.Item.Function.Definition.DisplayName, StringComparer.Ordinal)
        .ThenBy(entry => entry.Item.Function.Definition.Version);

    private void Save(List<StudioGraphFunctionAsset> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        entries = updated;
    }

    private static bool IsIntact(StudioGraphFunctionAsset asset)
    {
        if (asset.FormatVersion != StudioGraphFunctionAsset.CurrentFormatVersion || asset.Item?.Function is not { } configuration || asset.Layout is null ||
            !GraphFunctionJson.Validate(configuration.Definition).IsEmpty ||
            configuration.ContentHash != GraphFunctionJson.ComputeHash(configuration.Definition)) return false;
        try
        {
            var draft = StudioGraphFunctionAsset.Draft(configuration.Definition);
            _ = WorkflowLayoutJson.Deserialize(WorkflowLayoutJson.Serialize(asset.Layout, draft), draft);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static List<StudioGraphFunctionAsset> LoadEntries(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The graph function library must contain an array.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64
        };
        return document.RootElement.EnumerateArray().Select(element =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(element);
            return StudioGraphFunctionAsset.Deserialize(bytes, options);
        }).ToList();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The graph function library contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
        }
    }

    internal static void Validate(GraphFunctionDefinition function, string folder)
    {
        ArgumentNullException.ThrowIfNull(function);
        var issues = GraphFunctionJson.Validate(function);
        if (!issues.IsEmpty) throw new ArgumentException(string.Join(" ", issues.Select(issue => issue.Message)));
        ValidateFolder(folder);
    }

    private static void ValidateFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Length > 256 || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 8 ||
            folder.Split('/').Any(part => part is "." or ".." || part.Length > 64 || part.Contains('\\')))
            throw new ArgumentException("Use a folder path with at most eight short segments.");
    }

    private static bool IsValidFolder(string? folder)
    {
        try { ValidateFolder(folder!); return true; }
        catch (ArgumentException) { return false; }
    }
}
