using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Presentation metadata only. Never changes execution order, capabilities, or definition hashes.</summary>
public sealed record WorkflowLayout(string SchemaVersion,
    ImmutableSortedDictionary<string, NodePresentation> Nodes,
    ImmutableSortedDictionary<string, WirePresentation> Wires,
    ImmutableSortedDictionary<string, GraphSection> Sections)
{
    public static WorkflowLayout Create(WorkflowDefinition definition)
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodePresentation>(StringComparer.Ordinal);
        var order = WorkflowGraph.DisplayOrder(definition);
        if (WorkflowGraph.HasRouting(definition))
        {
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new Dictionary<int, int>();
            foreach (var id in order)
            {
                var parents = definition.Connections.Values.Where(e => e.Target.NodeId == id && columns.ContainsKey(e.Source.NodeId)).Select(e => columns[e.Source.NodeId]);
                var column = parents.DefaultIfEmpty(-1).Max() + 1;
                columns[id] = column;
                var row = rows.GetValueOrDefault(column);
                rows[column] = row + 1;
                // Fold very deep graphs within the same coordinate bound as linear layouts.
                nodes[id] = new(new(80 + column % 40 * 360, 100 + (column / 40 * 16 + row) * 700));
            }
        }
        else
        {
            for (var index = 0; index < order.Length; index++)
                nodes[order[index]] = new(new(80 + index % 4 * 360, 100 + index / 4 * 700));
        }
        if (nodes.Values.Any(n => n.Position.X > WorkflowLayoutJson.MaximumCoordinate || n.Position.Y > WorkflowLayoutJson.MaximumCoordinate))
            for (var index = 0; index < order.Length; index++)
                nodes[order[index]] = new(new(80 + index % 16 * 360, 100 + index / 16 * 700));
        return new("1.0", nodes.ToImmutable(),
            ImmutableSortedDictionary<string, WirePresentation>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableSortedDictionary<string, GraphSection>.Empty.WithComparers(StringComparer.Ordinal));
    }

    /// <summary>Retains surviving identities after semantic edits. Removed edges lose their routes.</summary>
    public WorkflowLayout Reconcile(WorkflowDefinition definition)
    {
        var defaults = Create(definition);
        var reconciled = defaults.Nodes.ToImmutableSortedDictionary(p => p.Key, p => Nodes.GetValueOrDefault(p.Key) ?? p.Value, StringComparer.Ordinal);
        // Layout reconciliation must also place newly added, unconnected processing
        // nodes away from existing nodes in otherwise linear graphs.
        {
            var occupied = Nodes.Where(p => definition.Nodes.ContainsKey(p.Key)).Select(p => p.Value.Position).ToList();
            foreach (var id in WorkflowGraph.DisplayOrder(definition).Where(id => !Nodes.ContainsKey(id)))
            {
                var point = defaults.Nodes[id].Position;
                while (occupied.Any(p => Math.Abs(p.X - point.X) < 300 && Math.Abs(p.Y - point.Y) < 650))
                {
                    point = point.Y + 700 <= WorkflowLayoutJson.MaximumCoordinate
                        ? point with { Y = point.Y + 700 } : new(point.X + 360, 100);
                    if (point.X > WorkflowLayoutJson.MaximumCoordinate) throw new ArgumentException("No bounded default position is available for the new node.");
                }
                occupied.Add(point);
                reconciled = reconciled.SetItem(id, reconciled[id] with { Position = point });
            }
        }
        return this with
        {
            Nodes = reconciled,
            Wires = Wires.Where(p => WorkflowSchemaVersions.UsesExplicitGraph(definition.SchemaVersion)
                ? p.Key.StartsWith("data:", StringComparison.Ordinal) && definition.Connections.ContainsKey(p.Key["data:".Length..]) ||
                    p.Key.StartsWith("execution:", StringComparison.Ordinal) && definition.ExecutionConnections?.ContainsKey(p.Key["execution:".Length..]) == true
                : definition.Connections.ContainsKey(p.Key)).ToImmutableSortedDictionary(StringComparer.Ordinal),
            Sections = Sections.Select(p => KeyValuePair.Create(p.Key, p.Value with
                { NodeIds = p.Value.NodeIds.Where(definition.Nodes.ContainsKey).ToImmutableArray() }))
                .Where(p => p.Value.NodeIds.Length > 0).ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
    }
}

public sealed record GraphPoint(double X, double Y);
public sealed record NodePresentation(GraphPoint Position, string Label = "", string Comment = "");
/// <summary>Angle is an optional pinned tangent in degrees, clockwise in screen coordinates.</summary>
public sealed record WireWaypoint(GraphPoint Position, double? Angle = null);
public sealed record WirePresentation(string Style, ImmutableArray<WireWaypoint> Points);
public sealed record GraphSection(string Label, ImmutableArray<string> NodeIds);
public sealed record WorkflowDesign(WorkflowDraft Draft, WorkflowLayout Layout);

/// <summary>Optional host extension for atomic definition/layout saves and immutable published layouts.</summary>
public interface IWorkflowDesignCatalog : IWorkflowCatalog
{
    Task<WorkflowDesign?> GetDesignAsync(string workflowId);
    Task<WorkflowLayout?> GetRevisionLayoutAsync(Guid revisionId);
    Task<WorkflowDesign> SaveDesignAsync(WorkflowDefinition definition, WorkflowLayout layout, long expectedGeneration);
}

public static class WorkflowLayoutJson
{
    public const double MaximumCoordinate = 32768;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false, MaxDepth = 16, WriteIndented = true
    };

    public static void Validate(WorkflowLayout layout, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.SchemaVersion != "1.0" || layout.Nodes is null || layout.Wires is null || layout.Sections is null)
            throw new ArgumentException("Unsupported or incomplete graph layout.");
        if (layout.Nodes.Count != definition.Nodes.Count || layout.Nodes.Keys.Except(definition.Nodes.Keys).Any() ||
            layout.Wires.Count > (WorkflowSchemaVersions.UsesExplicitGraph(definition.SchemaVersion) ? definition.Connections.Count + (definition.ExecutionConnections?.Count ?? 0) : definition.Connections.Count) || layout.Sections.Count > 64)
            throw new ArgumentException("Layout must reference only the current definition and at most 64 sections.");
        foreach (var node in layout.Nodes.Values)
        {
            if (node is null || node.Label is null || node.Comment is null || node.Label.Length > 160 || node.Comment.Length > 4000)
                throw new ArgumentException("Node labels/comments exceed their limits.");
            Point(node.Position);
        }
        foreach (var (id, wire) in layout.Wires)
        {
            var knownConnection = WorkflowSchemaVersions.UsesExplicitGraph(definition.SchemaVersion)
                ? id.StartsWith("data:", StringComparison.Ordinal) && definition.Connections.ContainsKey(id["data:".Length..]) ||
                    id.StartsWith("execution:", StringComparison.Ordinal) && definition.ExecutionConnections?.ContainsKey(id["execution:".Length..]) == true
                : definition.Connections.ContainsKey(id);
            if (!knownConnection || wire is null || wire.Style is not ("curve" or "orthogonal") ||
                wire.Points.IsDefault || wire.Points.Length > 32)
                throw new ArgumentException("Invalid wire route; at most 32 waypoints are supported.");
            foreach (var point in wire.Points)
            {
                if (point is null || point.Angle is { } angle && (!double.IsFinite(angle) || angle < 0 || angle >= 360))
                    throw new ArgumentException("Invalid pinned wire angle.");
                Point(point.Position);
                if (wire.Style == "orthogonal" && point.Angle is { } direction && direction % 90 != 0)
                    throw new ArgumentException("Orthogonal routes accept only right-angle tangents.");
            }
        }
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, section) in layout.Sections)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || section is null || string.IsNullOrWhiteSpace(section.Label) ||
                section.Label.Length > 160 || section.NodeIds.IsDefaultOrEmpty || section.NodeIds.Length > definition.Nodes.Count)
                throw new ArgumentException("Invalid section label or membership.");
            foreach (var nodeId in section.NodeIds)
                if (nodeId is null || !definition.Nodes.ContainsKey(nodeId) || !members.Add(nodeId))
                    throw new ArgumentException("A node can belong to one section only.");
        }
    }

    private static void Point(GraphPoint? point)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.Y < 0 || point.X > MaximumCoordinate || point.Y > MaximumCoordinate)
            throw new ArgumentException("Layout coordinates must be finite and within the canvas bounds.");
    }

    public static string Serialize(WorkflowLayout layout, WorkflowDefinition definition)
    {
        Validate(layout, definition);
        var normalized = layout with
        {
            Nodes = layout.Nodes.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Wires = layout.Wires.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Sections = layout.Sections.ToImmutableSortedDictionary(p => p.Key,
                p => p.Value with { NodeIds = p.Value.NodeIds.Order(StringComparer.Ordinal).ToImmutableArray() }, StringComparer.Ordinal)
        };
        var json = JsonSerializer.Serialize(normalized, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new ArgumentException("Layout exceeds 1 MiB.");
        return json;
    }

    public static WorkflowLayout Deserialize(string json, WorkflowDefinition definition)
    {
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new ArgumentException("Layout exceeds 1 MiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var layout = JsonSerializer.Deserialize<WorkflowLayout>(json, Options) ?? throw new JsonException("Layout required.");
        Validate(layout, definition);
        return layout;
    }

    public static string ComputeHash(WorkflowLayout layout, WorkflowDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(layout, definition))));

    /// <summary>Accepts the canonical layout hash and the prior Windows CRLF hash for persisted packages.</summary>
    public static bool MatchesHash(WorkflowLayout layout, WorkflowDefinition definition, string? hash)
    {
        if (hash is null) return false;
        var serialized = Serialize(layout, definition);
        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        if (string.Equals(Hash(serialized), hash, StringComparison.Ordinal)) return true;
        // Before portable serialization, Windows hashed the same JSON with CRLF indentation.
        return string.Equals(Hash(serialized.Replace("\n", "\r\n", StringComparison.Ordinal)), hash, StringComparison.Ordinal);
    }

}
