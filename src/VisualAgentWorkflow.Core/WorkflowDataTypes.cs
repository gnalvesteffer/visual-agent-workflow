using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public static class WorkflowDataTypes
{
    public const string String = "string";
    public const string Boolean = "boolean";
    public const string Integer = "integer";
    public const string Json = "json";
}

/// <summary>Versioned metadata for a data type that can cross a portable workflow pin.</summary>
public sealed record WorkflowDataTypeContract(
    string Id,
    string TypeVersion,
    string DisplayName,
    string Description,
    bool IsContainer,
    bool SupportsNull,
    bool SupportsImplicitConversion);

/// <summary>Canonical type identities and compatibility rules for graph data values.</summary>
public static class WorkflowDataTypeCatalog
{
    public static ImmutableArray<WorkflowDataTypeContract> All { get; } =
    [
        new(WorkflowDataTypes.String, "1.0.0", "String", "Bounded UTF-16 text value.", false, false, false),
        new(WorkflowDataTypes.Boolean, "1.0.0", "Boolean", "True or false value.", false, false, false),
        new(WorkflowDataTypes.Integer, "1.0.0", "Integer", "Signed 64-bit integer value.", false, false, false),
        new(WorkflowDataTypes.Json, "1.0.0", "JSON", "Bounded structured JSON value.", true, false, false)
    ];

    public static bool IsSupported(string? type) =>
        type is not null && All.Any(candidate => string.Equals(candidate.Id, type, StringComparison.Ordinal));

    /// <summary>Connections are compatible only when both pins name the same supported type.</summary>
    public static bool AreCompatible(string? sourceType, string? targetType) =>
        IsSupported(sourceType) && string.Equals(sourceType, targetType, StringComparison.Ordinal);

    public static WorkflowDataTypeContract Get(string type) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.Id, type, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown workflow data type '{type}'.");
}
