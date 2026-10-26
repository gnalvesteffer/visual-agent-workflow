using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Local authoring catalog for immutable pure graph-function revisions; workflows embed exact snapshots.</summary>
public sealed class StudioGraphFunctionLibrary : IGraphFunctionCatalog
{
    private readonly object gate = new();
    private readonly string path;
    private List<GraphFunctionCatalogEntry> entries;

    public StudioGraphFunctionLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "graph-functions.json");
        entries = File.Exists(path)
            ? JsonSerializer.Deserialize<List<GraphFunctionCatalogEntry>>(File.ReadAllText(path)) ?? []
            : [];
        if (entries.Any(entry => entry is null || !IsIntact(entry.Function) || !IsValidFolder(entry.Folder)) ||
            entries.GroupBy(entry => (entry.Function.Definition.FunctionId, entry.Function.Definition.Version)).Any(group => group.Count() > 1))
            throw new InvalidDataException("The graph function library contains an invalid or modified revision.");
    }

    public IReadOnlyList<GraphFunctionCatalogEntry> List()
    {
        lock (gate) return entries.OrderBy(entry => entry.Folder, StringComparer.Ordinal)
            .ThenBy(entry => entry.Function.Definition.DisplayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Function.Definition.Version).ToArray();
    }

    public void Add(GraphFunctionDefinition function, string folder)
    {
        Validate(function, folder);
        var configuration = new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function));
        lock (gate)
        {
            if (entries.Any(entry => entry.Function.Definition.FunctionId == function.FunctionId &&
                entry.Function.Definition.Version == function.Version))
                throw new ArgumentException("This graph function version already exists. Create a new version for edits.");
            Save([.. entries, new(configuration, folder)]);
        }
    }

    public bool Move(string functionId, int version, string folder)
    {
        ValidateFolder(folder);
        lock (gate)
        {
            var index = entries.FindIndex(entry => entry.Function.Definition.FunctionId == functionId &&
                entry.Function.Definition.Version == version);
            if (index < 0) return false;
            var updated = entries.ToList();
            updated[index] = updated[index] with { Folder = folder };
            Save(updated);
            return true;
        }
    }

    public bool Delete(string functionId, int version)
    {
        lock (gate)
        {
            var updated = entries.Where(entry => entry.Function.Definition.FunctionId != functionId ||
                entry.Function.Definition.Version != version).ToList();
            if (updated.Count == entries.Count) return false;
            Save(updated);
            return true;
        }
    }

    private void Save(List<GraphFunctionCatalogEntry> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        entries = updated;
    }

    private static bool IsIntact(GraphFunctionConfiguration? configuration) => configuration is not null &&
        GraphFunctionJson.Validate(configuration.Definition).IsEmpty &&
        configuration.ContentHash == GraphFunctionJson.ComputeHash(configuration.Definition);

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
