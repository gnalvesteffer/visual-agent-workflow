using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Deterministic immutable operations over bounded primitive typed arrays.</summary>
public static class WorkflowTypedArrayOperations
{
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
        if (!WorkflowDataTypes.IsArrayElementType(elementType) || elementType == WorkflowDataTypes.Json ||
            array.ValueKind != JsonValueKind.Array ||
            !WorkflowArrayLiteral.TryNormalizeValue(elementType, array.GetRawText(), out _, out _))
            throw new ArgumentException("A bounded primitive typed array is required.", nameof(array));
    }

    private static JsonElement ToElement(string elementType, object item)
    {
        var value = item switch
        {
            string text when elementType == WorkflowDataTypes.String => JsonSerializer.SerializeToElement(text),
            bool boolean when elementType == WorkflowDataTypes.Boolean => JsonSerializer.SerializeToElement(boolean),
            long integer when elementType == WorkflowDataTypes.Integer => JsonSerializer.SerializeToElement(integer),
            double number when elementType == WorkflowDataTypes.Number && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
            _ => throw new ArgumentException($"The array item must be a {elementType} value.", nameof(item))
        };
        return value;
    }

    private static JsonElement Normalize(string elementType, IEnumerable<JsonElement> values)
    {
        var candidate = JsonSerializer.Serialize(values);
        if (!WorkflowArrayLiteral.TryNormalizeValue(elementType, candidate, out var normalized, out var error))
            throw new ArgumentException(error, nameof(values));
        using var document = JsonDocument.Parse(normalized);
        return document.RootElement.Clone();
    }
}
