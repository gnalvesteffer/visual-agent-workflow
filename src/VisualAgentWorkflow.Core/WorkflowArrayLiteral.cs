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
        values = default;
        error = "Array literal configuration is missing.";
        if (configuration is null || configuration.ValuesJson is null ||
            !WorkflowDataTypes.IsArrayElementType(configuration.ElementType) ||
            configuration.ElementType == WorkflowDataTypes.Json)
        {
            error = "Array literals require a supported primitive element type.";
            return false;
        }

        var valuesJson = configuration.ValuesJson;
        if (Encoding.UTF8.GetByteCount(valuesJson) > MaximumPayloadBytes)
        {
            error = $"Array literal exceeds {MaximumPayloadBytes} UTF-8 bytes.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(valuesJson, new JsonDocumentOptions { MaxDepth = 1 });
        }
        catch (JsonException)
        {
            error = "Array literal must be valid JSON containing only scalar values.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "Array literal must have a JSON array root.";
                return false;
            }

            if (root.GetArrayLength() > WorkflowJsonArrayLimits.MaximumItems)
            {
                error = $"Array literal exceeds {WorkflowJsonArrayLimits.MaximumItems} items.";
                return false;
            }

            foreach (var item in root.EnumerateArray())
            {
                if (!Matches(configuration.ElementType, item))
                {
                    error = $"Every array item must be a non-null {configuration.ElementType} value.";
                    return false;
                }
            }

            values = root.Clone();
            error = string.Empty;
            return true;
        }
    }

    private static bool Matches(string elementType, JsonElement value) => elementType switch
    {
        WorkflowDataTypes.String => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16_384,
        WorkflowDataTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        WorkflowDataTypes.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        WorkflowDataTypes.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        _ => false
    };
}
