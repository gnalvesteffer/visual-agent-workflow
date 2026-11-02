using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Project-local immutable workflow interface and implementation revisions.</summary>
public sealed class StudioWorkflowInterfaceLibrary
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
    private readonly object gate;
    private readonly string path;
    private List<WorkflowInterfaceRevision> interfaces;
    private List<WorkflowInterfaceImplementationRevision> implementations;

    public StudioWorkflowInterfaceLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "workflow-interfaces.json");
        gate = Gates.GetOrAdd(Path.GetFullPath(path), static _ => new object());
        (interfaces, implementations) = File.Exists(path) ? Load(File.ReadAllText(path)) : ([], []);
        ValidateContents();
    }

    private void ValidateContents()
    {
        if (interfaces.Any(revision => revision is null || !IsValid(revision)) ||
            implementations.Any(revision => revision is null || !IsValid(revision)) ||
            interfaces.GroupBy(revision => (revision.Definition.InterfaceId, revision.Version)).Any(group => group.Count() > 1) ||
            implementations.GroupBy(revision => (revision.Definition.ImplementationId, revision.Version)).Any(group => group.Count() > 1) ||
            interfaces.Select(revision => revision.RevisionId).Concat(implementations.Select(revision => revision.RevisionId))
                .GroupBy(id => id).Any(group => group.Count() > 1) ||
            implementations.Any(revision => !HasInterface(revision.Definition.Interface, interfaces)))
            throw new InvalidDataException("The workflow interface library contains an invalid or duplicate revision.");
    }

    public IReadOnlyList<WorkflowInterfaceRevision> ListInterfaceRevisions()
    {
        lock (gate)
        {
            Refresh();
            return interfaces.OrderBy(revision => revision.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(revision => revision.Version).ToArray();
        }
    }

    public IReadOnlyList<WorkflowInterfaceImplementationRevision> ListImplementationRevisions()
    {
        lock (gate)
        {
            Refresh();
            return implementations.OrderBy(revision => revision.Definition.ImplementationId, StringComparer.Ordinal)
                .ThenBy(revision => revision.Version).ToArray();
        }
    }

    public void Add(WorkflowInterfaceRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        lock (gate)
        {
            Refresh();
            if (interfaces.Any(existing => existing.Definition.InterfaceId == revision.Definition.InterfaceId && existing.Version == revision.Version))
                throw new ArgumentException("This workflow interface version already exists.", nameof(revision));
            if (interfaces.Any(existing => existing.RevisionId == revision.RevisionId) ||
                implementations.Any(existing => existing.RevisionId == revision.RevisionId))
                throw new ArgumentException("This workflow interface revision ID already exists.", nameof(revision));
            Save([.. interfaces, revision], implementations);
        }
    }

    public void Add(WorkflowInterfaceImplementationRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.Validate();
        lock (gate)
        {
            Refresh();
            if (implementations.Any(existing => existing.Definition.ImplementationId == revision.Definition.ImplementationId && existing.Version == revision.Version))
                throw new ArgumentException("This workflow interface implementation version already exists.", nameof(revision));
            if (implementations.Any(existing => existing.RevisionId == revision.RevisionId) ||
                interfaces.Any(existing => existing.RevisionId == revision.RevisionId))
                throw new ArgumentException("This workflow interface revision ID already exists.", nameof(revision));
            if (!HasInterface(revision.Definition.Interface, interfaces))
                throw new ArgumentException("The implementation references an interface revision that is not in this project.", nameof(revision));
            Save(interfaces, [.. implementations, revision]);
        }
    }

    private void Save(List<WorkflowInterfaceRevision> updatedInterfaces,
        List<WorkflowInterfaceImplementationRevision> updatedImplementations)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new LibraryDocument(updatedInterfaces, updatedImplementations)));
        File.Move(temporary, path, true);
        interfaces = updatedInterfaces;
        implementations = updatedImplementations;
    }

    private void Refresh()
    {
        if (!File.Exists(path))
        {
            interfaces = [];
            implementations = [];
            return;
        }

        (interfaces, implementations) = Load(File.ReadAllText(path));
        ValidateContents();
    }

    private static (List<WorkflowInterfaceRevision>, List<WorkflowInterfaceImplementationRevision>) Load(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64
        };
        var library = JsonSerializer.Deserialize<LibraryDocument>(json, options)
            ?? throw new InvalidDataException("The workflow interface library is empty.");
        return (library.Interfaces ?? [], library.Implementations ?? []);
    }

    private static bool IsValid(WorkflowInterfaceRevision revision)
    {
        try { revision.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsValid(WorkflowInterfaceImplementationRevision revision)
    {
        try { revision.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool HasInterface(WorkflowInterfaceRevision contract, IEnumerable<WorkflowInterfaceRevision> revisions) =>
        revisions.Any(revision => revision.Definition.InterfaceId == contract.Definition.InterfaceId &&
            revision.RevisionId == contract.RevisionId && revision.ContentHash == contract.ContentHash);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The workflow interface library contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed record LibraryDocument(List<WorkflowInterfaceRevision>? Interfaces,
        List<WorkflowInterfaceImplementationRevision>? Implementations);
}
