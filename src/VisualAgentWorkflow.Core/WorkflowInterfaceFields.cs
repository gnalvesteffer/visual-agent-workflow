using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Strict JSON object adapter for named fields at a schema-1.1 interface boundary.</summary>
public static class WorkflowInterfaceFields
{
    public const int MaximumBoundaryJsonBytes = 1_048_576;

    /// <summary>Encodes a value already resolved from a matching graph pin.</summary>
    public static JsonElement ToJsonElement(string dataType, object value)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        ArgumentNullException.ThrowIfNull(value);
        var element = value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value);
        var field = new WorkflowInterfaceDataField("value", "Value", dataType);
        ValidateValue(field, element, "input");
        JsonDocumentSafety.RejectDuplicateProperties(element);
        return element;
    }

    /// <summary>Decodes a validated JSON property to the runtime value used by its graph pin.</summary>
    public static object ToWorkflowValue(string dataType, JsonElement value)
    {
        var field = new WorkflowInterfaceDataField("value", "Value", dataType);
        ValidateValue(field, value, "output");
        return dataType switch
        {
            WorkflowDataTypes.String => value.GetString()!,
            WorkflowDataTypes.Boolean => value.GetBoolean(),
            WorkflowDataTypes.Integer => value.GetInt64(),
            WorkflowDataTypes.Number => value.GetDouble(),
            WorkflowDataTypes.Json or WorkflowDataTypes.JsonArray => value.Clone(),
            _ => throw new ArgumentException($"Unsupported interface field type '{dataType}'.", nameof(dataType))
        };
    }

    /// <summary>Packages each required input exactly once, using field ID order for stable JSON.</summary>
    public static string PackInputs(WorkflowInterfaceOperation operation,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(values);
        var fields = GetFields(operation.Inputs, "input");
        ValidateExactFieldSet(fields, values.Keys, "input");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var field in fields.OrderBy(field => field.FieldId, StringComparer.Ordinal))
            {
                var value = values[field.FieldId];
                ValidateValue(field, value, "input");
                JsonDocumentSafety.RejectDuplicateProperties(value);
                writer.WritePropertyName(field.FieldId);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        if (output.Length > MaximumBoundaryJsonBytes)
            throw new ArgumentException("Interface input exceeds the 1 MiB JSON boundary limit.", nameof(values));
        var packed = Encoding.UTF8.GetString(output.ToArray());
        using var bounded = JsonDocument.Parse(packed, new JsonDocumentOptions { MaxDepth = 32 });
        return packed;
    }

    /// <summary>Validates and extracts all declared outputs; unknown or missing properties fail closed.</summary>
    public static ImmutableSortedDictionary<string, JsonElement> ProjectOutputs(
        WorkflowInterfaceOperation operation, string childOutputJson)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(childOutputJson);
        if (Encoding.UTF8.GetByteCount(childOutputJson) > MaximumBoundaryJsonBytes)
            throw new ArgumentException("Interface output exceeds the 1 MiB JSON boundary limit.", nameof(childOutputJson));

        using var document = JsonDocument.Parse(childOutputJson, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The child workflow must return a JSON object for interface outputs.", nameof(childOutputJson));

        var fields = GetFields(operation.Outputs, "output");
        var supplied = root.EnumerateObject().Select(property => property.Name).ToArray();
        ValidateExactFieldSet(fields, supplied, "output");

        var result = ImmutableSortedDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var value = root.GetProperty(field.FieldId);
            ValidateValue(field, value, "output");
            result.Add(field.FieldId, value.Clone());
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<WorkflowInterfaceDataField> GetFields(
        ImmutableArray<WorkflowInterfaceDataField>? fields, string direction) =>
        fields is { } declared && !declared.IsDefaultOrEmpty
            ? declared
            : throw new ArgumentException($"The operation has no declared {direction} field contract.");

    private static void ValidateExactFieldSet(ImmutableArray<WorkflowInterfaceDataField> fields,
        IEnumerable<string> suppliedIds, string direction)
    {
        var supplied = suppliedIds.ToArray();
        var expected = fields.Select(field => field.FieldId).ToHashSet(StringComparer.Ordinal);
        if (supplied.Length != expected.Count || supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Length ||
            supplied.Any(id => !expected.Contains(id)))
            throw new ArgumentException($"The {direction} fields must contain every declared field exactly once and no undeclared fields.");
    }

    private static void ValidateValue(WorkflowInterfaceDataField field, JsonElement value, string direction)
    {
        var valid = field.DataType switch
        {
            WorkflowDataTypes.String => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16_384,
            WorkflowDataTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            WorkflowDataTypes.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            WorkflowDataTypes.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            WorkflowDataTypes.Json => true,
            WorkflowDataTypes.JsonArray => value.ValueKind == JsonValueKind.Array,
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"The {direction} field '{field.FieldId}' must be a {field.DataType} JSON value.");
    }
}
