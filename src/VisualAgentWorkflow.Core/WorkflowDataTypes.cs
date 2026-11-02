using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public static class WorkflowDataTypes
{
    public const string String = "string";
    public const string Boolean = "boolean";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Json = "json";
    /// <summary>Bounded immutable ordered array whose elements retain JSON value shape.</summary>
    public const string JsonArray = "array<json>";

    private static readonly ImmutableHashSet<string> ArrayElementTypes =
        ImmutableHashSet.Create(String, Boolean, Integer, Number);

    public static string ArrayOf(string elementType) =>
        IsArrayElementType(elementType)
            ? $"array<{elementType}>"
            : throw new ArgumentException("Array elements must use a supported scalar or JSON type.", nameof(elementType));

    public static bool IsArrayElementType(string? type) =>
        type == Json || ArrayElementTypes.Contains(type ?? string.Empty);

    public static bool IsPrimitiveArrayType(string? type) =>
        TryGetArrayElementType(type, out var elementType) && elementType != Json;

    public static bool TryGetArrayElementType(string? type, out string elementType)
    {
        elementType = string.Empty;
        if (type is null || !type.StartsWith("array<", StringComparison.Ordinal) || !type.EndsWith('>'))
            return false;

        var candidate = type[6..^1];
        if (!IsArrayElementType(candidate)) return false;
        elementType = candidate;
        return true;
    }
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
        new(WorkflowDataTypes.Number, "1.0.0", "Number", "Finite IEEE-754 double value.", false, false, false),
        new(WorkflowDataTypes.Json, "1.0.0", "JSON", "Bounded structured JSON value.", true, false, false),
        new(WorkflowDataTypes.JsonArray, "1.0.0", "JSON array", "Bounded immutable ordered array of JSON values.", true, false, false),
        new("array<string>", "1.0.0", "String array", "Bounded immutable ordered array of strings.", true, false, false),
        new("array<boolean>", "1.0.0", "Boolean array", "Bounded immutable ordered array of Booleans.", true, false, false),
        new("array<integer>", "1.0.0", "Integer array", "Bounded immutable ordered array of signed 64-bit integers.", true, false, false),
        new("array<number>", "1.0.0", "Number array", "Bounded immutable ordered array of finite numbers.", true, false, false)
    ];

    public static bool IsSupported(string? type) =>
        type is not null && (All.Any(candidate => string.Equals(candidate.Id, type, StringComparison.Ordinal)) ||
            WorkflowDataTypes.TryGetArrayElementType(type, out _));

    /// <summary>Connections are compatible only when both pins name the same supported type.</summary>
    public static bool AreCompatible(string? sourceType, string? targetType) =>
        IsSupported(sourceType) && string.Equals(sourceType, targetType, StringComparison.Ordinal);

    public static WorkflowDataTypeContract Get(string type) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.Id, type, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown workflow data type '{type}'.");
}
