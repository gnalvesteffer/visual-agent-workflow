using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// One field of a closed JSON object shape (Decision 0082, P5). The stable <see cref="Id"/> is the key in the value and the
/// handle every node uses; <see cref="Name"/> is display text only, so renaming never changes wiring or stored values.
/// </summary>
public sealed record JsonObjectFieldShape(
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("type"), JsonRequired] string Type,
    [property: JsonPropertyName("required"), JsonRequired] bool Required);

/// <summary>
/// Closed-object annotation for a <c>json</c> boundary: 1 to <see cref="JsonObjectShapeLimits.MaximumFields"/> flat scalar
/// fields. The wire type stays <c>json</c>; the shape is an additional constraint checked where a value enters the graph.
/// </summary>
public sealed record JsonObjectShape(
    [property: JsonPropertyName("fields"), JsonRequired] ImmutableArray<JsonObjectFieldShape> Fields);

public static class JsonObjectShapeLimits
{
    public const int MaximumFields = 32;
    public const int MaximumIdCharacters = 64;
    public const int MaximumNameCharacters = JsonProjectionLimits.MaximumNameCharacters;
    public const int MaximumPayloadCharacters = JsonProjectionLimits.MaximumPayloadCharacters;
    public const int MaximumDepth = 32;
}

/// <summary>Pure validation of shapes and of values against a shape. No I/O and no host state.</summary>
public static class JsonObjectShapes
{
    public static bool IsFieldType(string? type) =>
        type is WorkflowDataTypes.String or WorkflowDataTypes.Integer or WorkflowDataTypes.Number or WorkflowDataTypes.Boolean;

    /// <summary>A stable field ID starts with an ASCII letter and continues with ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsValidFieldId(string? id)
    {
        if (id is not { Length: > 0 and <= JsonObjectShapeLimits.MaximumIdCharacters } || !char.IsAsciiLetter(id[0])) return false;
        foreach (var character in id) if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return false;
        return true;
    }

    /// <summary>Returns the first structural problem with a shape, or null when the shape is well formed.</summary>
    public static string? Validate(JsonObjectShape? shape)
    {
        if (shape is null || shape.Fields.IsDefault) return "A JSON object shape requires a field list.";
        if (shape.Fields.Length is 0 or > JsonObjectShapeLimits.MaximumFields)
            return $"A JSON object shape requires 1 to {JsonObjectShapeLimits.MaximumFields} fields.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in shape.Fields)
        {
            if (field is null) return "A JSON object shape field cannot be null.";
            if (!IsValidFieldId(field.Id))
                return $"Field ID '{field.Id}' must be 1 to {JsonObjectShapeLimits.MaximumIdCharacters} ASCII letters, digits, '-' or '_' and start with a letter.";
            if (!ids.Add(field.Id)) return $"Field ID '{field.Id}' is declared more than once.";
            if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > JsonObjectShapeLimits.MaximumNameCharacters)
                return $"Field '{field.Id}' needs a display name of 1 to {JsonObjectShapeLimits.MaximumNameCharacters} characters.";
            if (!IsFieldType(field.Type))
                return $"Field '{field.Id}' has unsupported type '{field.Type}'; use string, integer, number or boolean.";
        }
        return null;
    }

    public static JsonObjectFieldShape? Find(JsonObjectShape? shape, string? fieldId) =>
        shape is { Fields.IsDefault: false } ? shape.Fields.FirstOrDefault(field => field is not null && string.Equals(field.Id, fieldId, StringComparison.Ordinal)) : null;

    /// <summary>
    /// Checks one JSON text against a shape: a single object, no duplicate keys, no extra keys, every required field present,
    /// and every present field exactly its declared scalar type (null is never accepted). Returns a clear error or null.
    /// </summary>
    public static string? ValidateValue(JsonObjectShape shape, string json)
    {
        try { return ValidateValueCore(shape, json); }
        catch (InvalidOperationException) { return "The value contains text that is not valid Unicode (an unpaired surrogate escape)."; }
    }

    /// <summary>Caller-controlled key names never enter messages whole: at most 64 characters, then an ellipsis.</summary>
    private static string Safe(string name) => name.Length <= 64 ? name : name[..64] + "…";

    private static string? ValidateValueCore(JsonObjectShape shape, string json)
    {
        if (json.Length > JsonObjectShapeLimits.MaximumPayloadCharacters)
            return $"JSON object exceeds {JsonObjectShapeLimits.MaximumPayloadCharacters} characters.";
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = JsonObjectShapeLimits.MaximumDepth }); }
        catch (JsonException) { return $"The value must be valid JSON with a maximum depth of {JsonObjectShapeLimits.MaximumDepth}."; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "The value must be a JSON object.";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return $"Field '{Safe(property.Name)}' appears more than once.";
                var field = Find(shape, property.Name);
                if (field is null) return $"Field '{Safe(property.Name)}' is not declared by the shape.";
                if (!MatchesType(property.Value, field.Type))
                    return $"Field '{field.Id}' must be {Article(field.Type)} {field.Type} value.";
            }
            foreach (var field in shape.Fields)
                if (field.Required && !seen.Contains(field.Id)) return $"Required field '{field.Id}' is missing.";
        }
        return null;
    }

    /// <summary>Reads one declared field as its typed CLR value (string, long, double or bool); null when absent.</summary>
    public static bool TryReadField(JsonObjectShape shape, string json, string fieldId, out object? value, out string? error)
    {
        value = null;
        error = ValidateValue(shape, json);
        if (error is not null) return false;
        var field = Find(shape, fieldId);
        if (field is null) { error = $"Field '{fieldId}' is not declared by the shape."; return false; }
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = JsonObjectShapeLimits.MaximumDepth });
        if (!document.RootElement.TryGetProperty(fieldId, out var element))
        {
            error = $"Field '{fieldId}' is absent from the object.";
            return false;
        }
        value = field.Type switch
        {
            WorkflowDataTypes.String => element.GetString(),
            WorkflowDataTypes.Integer => element.GetInt64(),
            WorkflowDataTypes.Number => element.GetDouble(),
            _ => element.GetBoolean()
        };
        return true;
    }

    private static bool MatchesType(JsonElement element, string type) => type switch
    {
        WorkflowDataTypes.String => element.ValueKind == JsonValueKind.String && element.GetString() is not null,
        WorkflowDataTypes.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
        WorkflowDataTypes.Integer => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
        WorkflowDataTypes.Number => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number),
        _ => false
    };

    private static string Article(string type) => type is WorkflowDataTypes.Integer ? "an" : "a";
}

/// <summary>Configuration of a <see cref="NodeTypes.ObjectGet"/> node: the stable field ID and the scalar type derived from the shape.</summary>
public sealed record ObjectGetConfiguration(
    [property: JsonPropertyName("fieldId"), JsonRequired] string FieldId,
    [property: JsonPropertyName("dataType"), JsonRequired] string DataType);
