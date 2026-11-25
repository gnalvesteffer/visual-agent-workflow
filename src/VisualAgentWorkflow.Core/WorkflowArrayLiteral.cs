using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

public static class WorkflowArrayLiteral
{
    public const int MaximumPayloadBytes = 16_384;

    public static bool TryParse(
        ArrayLiteralConfiguration? configuration,
        out JsonElement values,
        out string error)
    {
        if (configuration is null)
        {
            values = default;
            error = "Array literal configuration is missing.";
            return false;
        }

        return TryParseValue(configuration.ElementType, configuration.ValuesJson, out values, out error);
    }

    public static bool TryParseValue(string elementType, string? valuesJson,
        out JsonElement values, out string error)
    {
        values = default;
        error = "A typed array requires a supported element type.";
        if (valuesJson is null || !WorkflowDataTypes.IsArrayElementType(elementType))
        {
            if (valuesJson is null) error = "A typed array JSON value is required.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(valuesJson) > MaximumPayloadBytes)
        {
            error = $"Typed array exceeds {MaximumPayloadBytes} UTF-8 bytes.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(valuesJson, new JsonDocumentOptions { MaxDepth = WorkflowJsonArrayLimits.MaximumDepth });
        }
        catch (JsonException)
        {
            error = $"Typed array value must be valid JSON with maximum depth {WorkflowJsonArrayLimits.MaximumDepth}.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "Typed array value must have a JSON array root.";
                return false;
            }

            if (root.GetArrayLength() > WorkflowJsonArrayLimits.MaximumItems)
            {
                error = $"Typed array exceeds {WorkflowJsonArrayLimits.MaximumItems} items.";
                return false;
            }

            if (elementType == WorkflowDataTypes.Json)
            {
                try
                {
                    JsonDocumentSafety.RejectDuplicateProperties(root);
                }
                catch (JsonException exception)
                {
                    error = exception.Message;
                    return false;
                }
            }

            foreach (var item in root.EnumerateArray())
            {
                if (!Matches(elementType, item))
                {
                    error = elementType == WorkflowDataTypes.Json
                        ? "Every typed array item must be a valid JSON value."
                        : $"Every typed array item must be a non-null {elementType} value.";
                    return false;
                }
            }

            values = root.Clone();
            error = string.Empty;
            return true;
        }
    }

    public static bool TryNormalizeValue(string elementType, string? valuesJson,
        out string normalizedJson, out string error)
    {
        normalizedJson = string.Empty;
        if (!TryParseValue(elementType, valuesJson, out var values, out error)) return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in values.EnumerateArray())
            {
                switch (elementType)
                {
                    case WorkflowDataTypes.String:
                        WorkflowCanonicalValueEncoding.WriteString(writer, item.GetString());
                        break;
                    case WorkflowDataTypes.Boolean:
                        WorkflowCanonicalValueEncoding.WriteBoolean(writer, item.GetBoolean());
                        break;
                    case WorkflowDataTypes.Integer:
                        WorkflowCanonicalValueEncoding.WriteInteger(writer, item.GetInt64());
                        break;
                    case WorkflowDataTypes.Number:
                        WorkflowCanonicalValueEncoding.WriteNumber(writer, item.GetDouble());
                        break;
                    case WorkflowDataTypes.Json:
                        item.WriteTo(writer);
                        break;
                    default:
                        error = "A typed array requires a supported primitive element type.";
                        return false;
                }
            }
            writer.WriteEndArray();
        }

        var result = Encoding.UTF8.GetString(stream.ToArray());
        if (Encoding.UTF8.GetByteCount(result) > MaximumPayloadBytes)
        {
            error = $"Typed array result exceeds {MaximumPayloadBytes} UTF-8 bytes.";
            return false;
        }

        normalizedJson = result;
        error = string.Empty;
        return true;
    }

    private static bool Matches(string elementType, JsonElement value) => elementType switch
    {
        WorkflowDataTypes.String => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16_384,
        WorkflowDataTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        WorkflowDataTypes.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        WorkflowDataTypes.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        WorkflowDataTypes.Json => value.ValueKind != JsonValueKind.Undefined,
        _ => false
    };
}
