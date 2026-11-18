using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Equality rules shared by primitive graph comparisons and array membership.</summary>
public static class WorkflowPrimitiveValueEquality
{
    public static bool AreEqual(string dataType, object left, object right)
    {
        EnsureValue(dataType, left);
        EnsureValue(dataType, right);
        return dataType switch
        {
            WorkflowDataTypes.String => string.Equals((string)left, (string)right, StringComparison.Ordinal),
            WorkflowDataTypes.Boolean => (bool)left == (bool)right,
            WorkflowDataTypes.Integer => (long)left == (long)right,
            WorkflowDataTypes.Number => (double)left == (double)right,
            _ => throw new ArgumentException("Equality is defined only for primitive workflow values.", nameof(dataType))
        };
    }

    public static object FromJson(string dataType, JsonElement value) => dataType switch
    {
        WorkflowDataTypes.String when value.ValueKind == JsonValueKind.String => value.GetString()!,
        WorkflowDataTypes.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        WorkflowDataTypes.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) => integer,
        WorkflowDataTypes.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        _ => throw new ArgumentException($"The JSON value is not a valid {dataType} value.", nameof(value))
    };

    public static void EnsureValue(string dataType, object value)
    {
        var valid = (dataType, value) switch
        {
            (WorkflowDataTypes.String, string) => true,
            (WorkflowDataTypes.Boolean, bool) => true,
            (WorkflowDataTypes.Integer, long) => true,
            (WorkflowDataTypes.Number, double number) => double.IsFinite(number),
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"A finite primitive {dataType} value is required.", nameof(value));
    }
}
