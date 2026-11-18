using System.Text.Json;
using System.Text;

namespace VisualAgentWorkflow.Core;

/// <summary>Deterministic immutable operations over bounded typed arrays.</summary>
public static class WorkflowTypedArrayOperations
{
    public static bool Contains(string elementType, JsonElement array, object item)
    {
        if (!WorkflowDataTypes.IsPrimitiveArrayType(WorkflowDataTypes.ArrayOf(elementType)))
            throw new ArgumentException("Array membership supports only primitive element types.", nameof(elementType));
        EnsureArray(elementType, array);
        WorkflowPrimitiveValueEquality.EnsureValue(elementType, item);
        return array.EnumerateArray().Any(value => WorkflowPrimitiveValueEquality.AreEqual(elementType,
            WorkflowPrimitiveValueEquality.FromJson(elementType, value), item));
    }

    public static JsonElement Append(string elementType, JsonElement array, object item)
    {
        EnsureArray(elementType, array);
        return InsertAt(elementType, array, array.GetArrayLength(), item);
    }

    public static JsonElement InsertAt(string elementType, JsonElement array, long index, object item)
    {
        EnsureArray(elementType, array);
        if (index < 0 || index > array.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(index), index, "Insert index must be between zero and the array length.");

        var values = array.EnumerateArray().ToList();
        values.Insert((int)index, ToElement(elementType, item));
        return Normalize(elementType, values);
    }

    public static JsonElement ReplaceAt(string elementType, JsonElement array, long index, object item)
    {
        EnsureArray(elementType, array);
        if (index < 0 || index >= array.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(index), index, "Replace index must identify an existing array item.");

        var values = array.EnumerateArray().ToList();
        values[(int)index] = ToElement(elementType, item);
        return Normalize(elementType, values);
    }

    public static JsonElement RemoveAt(string elementType, JsonElement array, long index)
    {
        EnsureArray(elementType, array);
        if (index < 0 || index >= array.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(index), index, "Remove index must identify an existing array item.");

        var values = array.EnumerateArray().ToList();
        values.RemoveAt((int)index);
        return Normalize(elementType, values);
    }

    public static JsonElement Concatenate(string elementType, JsonElement left, JsonElement right)
    {
        EnsureArray(elementType, left);
        EnsureArray(elementType, right);
        return Normalize(elementType, left.EnumerateArray().Concat(right.EnumerateArray()));
    }

    private static void EnsureArray(string elementType, JsonElement array)
    {
        if (!WorkflowDataTypes.IsArrayElementType(elementType) ||
            array.ValueKind != JsonValueKind.Array ||
            !WorkflowArrayLiteral.TryNormalizeValue(elementType, array.GetRawText(), out _, out _))
            throw new ArgumentException("A bounded typed array is required.", nameof(array));
    }

    private static JsonElement ToElement(string elementType, object item)
    {
        var value = item switch
        {
            string text when elementType == WorkflowDataTypes.String => JsonSerializer.SerializeToElement(text),
            bool boolean when elementType == WorkflowDataTypes.Boolean => JsonSerializer.SerializeToElement(boolean),
            long integer when elementType == WorkflowDataTypes.Integer => JsonSerializer.SerializeToElement(integer),
            double number when elementType == WorkflowDataTypes.Number && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
            JsonElement json when elementType == WorkflowDataTypes.Json && json.ValueKind != JsonValueKind.Undefined => json.Clone(),
            _ => throw new ArgumentException($"The array item must be a {elementType} value.", nameof(item))
        };
        return value;
    }

    private static JsonElement Normalize(string elementType, IEnumerable<JsonElement> values)
    {
        var candidate = JsonSerializer.Serialize(values);
        if (Encoding.UTF8.GetByteCount(candidate) > WorkflowArrayLiteral.MaximumPayloadBytes)
            throw new ArgumentException($"Typed array result exceeds {WorkflowArrayLiteral.MaximumPayloadBytes} UTF-8 bytes.", nameof(values));
        if (!WorkflowArrayLiteral.TryNormalizeValue(elementType, candidate, out var normalized, out var error))
            throw new ArgumentException(error, nameof(values));
        using var document = JsonDocument.Parse(normalized);
        return document.RootElement.Clone();
    }
}
