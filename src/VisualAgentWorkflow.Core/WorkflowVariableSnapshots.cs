using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Versioned, trusted recovery state for one schema-1.44 variable store.</summary>
/// <remarks>This is runtime checkpoint state, not portable workflow data or ordinary playback evidence.</remarks>
public sealed record WorkflowVariableSnapshot(
    string FormatVersion,
    ImmutableArray<WorkflowVariableSnapshotEntry> Values,
    long NextVersion)
{
    public const string CurrentFormatVersion = "1.0";

    public string ComputeHash() => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}

public sealed record WorkflowVariableSnapshotEntry(
    string VariableId,
    string DataType,
    string Value,
    long Version);

public sealed partial class WorkflowVariableStore
{
    private const int MaximumSnapshotEntries = 64;
    private const int MaximumSnapshotValueLength = 16_384;

    /// <summary>Captures the complete scalar state and version counter under the store lock.</summary>
    public WorkflowVariableSnapshot Snapshot()
    {
        lock (sync)
        {
            return new(
                WorkflowVariableSnapshot.CurrentFormatVersion,
                values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new WorkflowVariableSnapshotEntry(
                        pair.Key,
                        pair.Value.DataType,
                        Format(pair.Value.DataType, pair.Value.Value),
                        pair.Value.Version))
                    .ToImmutableArray(),
                nextVersion);
        }
    }

    /// <summary>Restores a complete store only after validating its identity, types, bounds, and versions.</summary>
    public static WorkflowVariableStore FromSnapshot(WorkflowDefinition definition, WorkflowVariableSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FormatVersion != WorkflowVariableSnapshot.CurrentFormatVersion ||
            snapshot.Values.IsDefault || snapshot.Values.Length > MaximumSnapshotEntries || snapshot.NextVersion < 0)
            throw new ArgumentException("The variable snapshot format or bounds are invalid.", nameof(snapshot));

        var declarations = definition.Variables ?? ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty;
        if (snapshot.Values.Length != declarations.Count ||
            snapshot.Values.Select(entry => entry.VariableId).Distinct(StringComparer.Ordinal).Count() != snapshot.Values.Length ||
            !snapshot.Values.Select(entry => entry.VariableId).ToHashSet(StringComparer.Ordinal).SetEquals(declarations.Keys))
            throw new ArgumentException("The variable snapshot must contain exactly the declared variable IDs.", nameof(snapshot));

        var restored = new WorkflowVariableStore(definition);
        var values = new Dictionary<string, WorkflowVariableValue>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Values)
        {
            if (!declarations.TryGetValue(entry.VariableId, out var declaration) || declaration is null ||
                string.IsNullOrWhiteSpace(entry.DataType) || entry.Value.Length > MaximumSnapshotValueLength || entry.Version < 1 ||
                entry.Version > snapshot.NextVersion || !string.Equals(entry.DataType, declaration.Type ?? WorkflowDataTypes.String, StringComparison.Ordinal))
                throw new ArgumentException("The variable snapshot contains an invalid declaration, value, type, or version.", nameof(snapshot));
            object value;
            try { value = Parse(entry.DataType, entry.Value); }
            catch (ArgumentException) { throw new ArgumentException("The variable snapshot contains an invalid scalar value.", nameof(snapshot)); }
            values.Add(entry.VariableId, new(entry.DataType, value, entry.Version));
        }

        var maximumVersion = values.Values.Select(value => value.Version).DefaultIfEmpty(0).Max();
        if (snapshot.NextVersion < maximumVersion)
            throw new ArgumentException("The variable snapshot version counter precedes a stored value.", nameof(snapshot));
        lock (restored.sync)
        {
            restored.values.Clear();
            foreach (var pair in values) restored.values.Add(pair.Key, pair.Value);
            restored.nextVersion = snapshot.NextVersion;
        }
        return restored;
    }

    private static string Format(string dataType, object value) => dataType switch
    {
        WorkflowDataTypes.String when value is string text && text.Length <= MaximumSnapshotValueLength => text,
        WorkflowDataTypes.Boolean when value is bool boolean => boolean ? "true" : "false",
        WorkflowDataTypes.Integer when value is long integer => integer.ToString(CultureInfo.InvariantCulture),
        WorkflowDataTypes.Number when value is double number && double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("The variable store contains an unsupported scalar value.", nameof(value))
    };
}
