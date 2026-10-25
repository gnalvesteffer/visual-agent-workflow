using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Validated portable MCP input shapes supported by the agent adapter.</summary>
public static class McpToolInputSchemas
{
    /// <summary>Returns whether an MCP tool uses the supported bounded JSON schema subset.</summary>
    public static bool IsSupported(JsonElement schema) => McpInputSchema.IsSupported(schema);

    /// <summary>Returns a normalized schema suitable for an agent function declaration.</summary>
    public static JsonElement Normalize(JsonElement schema) => McpInputSchema.Normalize(schema);

    /// <summary>Returns a concise summary of supported fields, nested objects, and arrays.</summary>
    public static string Summarize(JsonElement schema) => McpInputSchema.Summarize(schema);

    /// <summary>Validates JSON arguments against the supported schema subset and converts them to MCP call values.</summary>
    public static IReadOnlyDictionary<string, object?> ToCallArguments(JsonElement schema, JsonElement arguments) =>
        McpInputSchema.Validate(schema).ToCallArguments(arguments);
}

internal sealed class McpInputSchema
{
    private const int MaximumSchemaLength = 16_384;
    private const int MaximumArgumentLength = 65_536;
    private const int MaximumDepth = 8;
    private const int MaximumProperties = 32;
    private const int MaximumArrayItems = 64;
    private const int MaximumValueCount = 1_024;
    private const int MaximumStringLength = 8_192;
    private const int MaximumObjectProperties = 128;
    private const int MaximumSummaryFields = 32;
    private const int MaximumSummaryLength = 4_096;
    private readonly JsonElement schema;

    private McpInputSchema(JsonElement schema) => this.schema = schema;

    internal static McpInputSchema Validate(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > MaximumSchemaLength ||
            !schema.TryGetProperty("type", out var rootType) || rootType.ValueKind != JsonValueKind.String || rootType.GetString() != "object")
            throw UnsupportedSchema();

        ValidateSchema(schema, depth: 0);
        return new(schema.Clone());
    }

    internal static JsonElement Normalize(JsonElement schema)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            WriteNormalizedSchema(writer, Validate(schema).schema);
        }

        using var normalized = JsonDocument.Parse(output.ToArray());
        return normalized.RootElement.Clone();
    }

    internal static bool IsSupported(JsonElement schema)
    {
        try { Validate(schema); return true; }
        catch (ToolInvocationException) { return false; }
    }

    internal static string Summarize(JsonElement schema)
    {
        var root = Validate(schema).schema;
        var fields = new List<string>();
        SummarizeObject(root, "", depth: 0, fields);
        var summary = string.Join(", ", fields);
        return summary.Length <= MaximumSummaryLength ? summary : summary[..(MaximumSummaryLength - 3)] + "...";
    }

    private static void SummarizeObject(JsonElement definition, string prefix, int depth, List<string> fields)
    {
        if (!definition.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            var path = string.IsNullOrEmpty(prefix) ? "(root arguments)" : prefix;
            fields.Add($"{path}: dynamic JSON object (bounded)");
            return;
        }
        var required = definition.TryGetProperty("required", out var requiredFields)
            ? requiredFields.EnumerateArray().Select(field => field.GetString()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string?>(StringComparer.Ordinal);

        foreach (var property in properties.EnumerateObject())
        {
            if (fields.Count >= MaximumSummaryFields) break;
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            var type = property.Value.TryGetProperty("type", out var typeSchema) ? typeSchema.GetString()! : "JSON value";
            var cardinality = required.Contains(property.Name) ? "required" : "optional";
            if (type == "object" && property.Value.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
            {
                fields.Add($"{path}: dynamic JSON object {cardinality} · bounded");
                continue;
            }
            if (type == "object" && depth < 4 && property.Value.TryGetProperty("properties", out var nestedProperties) && nestedProperties.ValueKind == JsonValueKind.Object && nestedProperties.EnumerateObject().Any())
            {
                SummarizeObject(property.Value, path, depth + 1, fields);
                continue;
            }

            if (type == "array") type = property.Value.GetProperty("items").TryGetProperty("type", out var itemType)
                ? $"array<{itemType.GetString()}>" : "array<JSON value>";
            fields.Add($"{path}: {type} {cardinality}");
        }
    }

    public IReadOnlyDictionary<string, object?> ToCallArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > MaximumArgumentLength)
            throw InvalidArguments();

        var visitedValues = 0;
        ValidateValue(schema, arguments, depth: 0, ref visitedValues);
        return arguments.EnumerateObject().ToDictionary(property => property.Name,
            property => ConvertValue(schema.GetProperty("properties").GetProperty(property.Name), property.Value),
            StringComparer.Ordinal);
    }

    private static void ValidateSchema(JsonElement definition, int depth)
    {
        if (IsAnySchema(definition)) return;
        if (depth > MaximumDepth || definition.ValueKind != JsonValueKind.Object ||
            !definition.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw UnsupportedSchema();

        var type = typeElement.GetString();
        var allowedFields = type switch
        {
            "object" => new[] { "type", "title", "description", "$schema", "properties", "required", "additionalProperties", "propertyNames" },
            "array" => new[] { "type", "title", "description", "items", "minItems", "maxItems" },
            "string" => new[] { "type", "title", "description", "enum", "minLength", "maxLength" },
            "integer" or "number" => new[] { "type", "title", "description", "enum", "minimum", "maximum" },
            "boolean" => new[] { "type", "title", "description", "enum" },
            _ => throw UnsupportedSchema()
        };

        if (definition.EnumerateObject().Any(field => !allowedFields.Contains(field.Name, StringComparer.Ordinal)))
            throw UnsupportedSchema();

        ValidateDescription(definition);
        ValidateEnum(definition, type!);

        switch (type)
        {
            case "object":
                ValidateObjectSchema(definition, depth);
                break;
            case "array":
                if (!definition.TryGetProperty("items", out var items)) throw UnsupportedSchema();
                ValidateArrayBounds(definition);
                ValidateSchema(items, depth + 1);
                break;
            case "string":
                ValidateStringBounds(definition);
                break;
            case "integer":
            case "number":
                ValidateNumberBounds(definition, type!);
                break;
        }
    }

    private static void ValidateObjectSchema(JsonElement definition, int depth)
    {
        var hasProperties = definition.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var hasAdditional = definition.TryGetProperty("additionalProperties", out var additional);
        if (hasProperties && properties.EnumerateObject().Count() > MaximumProperties ||
            hasAdditional && additional.ValueKind is not (JsonValueKind.False or JsonValueKind.Object) ||
            hasAdditional && additional.ValueKind == JsonValueKind.Object && !IsAnySchema(additional) ||
            definition.TryGetProperty("propertyNames", out var nameConstraint) && !IsStringNameSchema(nameConstraint))
            throw UnsupportedSchema();

        var declaredPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        if (hasProperties) foreach (var property in properties.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128 || !declaredPropertyNames.Add(property.Name))
                throw UnsupportedSchema();
            ValidateSchema(property.Value, depth + 1);
        }

        if (!definition.TryGetProperty("required", out var required)) return;
        if (!hasProperties) throw UnsupportedSchema();
        if (required.ValueKind != JsonValueKind.Array) throw UnsupportedSchema();
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in required.EnumerateArray())
            if (field.ValueKind != JsonValueKind.String || !properties.TryGetProperty(field.GetString()!, out _) ||
                !requiredNames.Add(field.GetString()!))
                throw UnsupportedSchema();

    }

    private static bool IsAnySchema(JsonElement schema) => schema.ValueKind == JsonValueKind.Object && !schema.EnumerateObject().Any();
    private static bool IsStringNameSchema(JsonElement schema) => schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "string" &&
        schema.EnumerateObject().All(field => field.Name is "type" or "minLength" or "maxLength");

    private static void WriteNormalizedSchema(Utf8JsonWriter writer, JsonElement definition)
    {
        if (IsAnySchema(definition)) { writer.WriteStartObject(); writer.WriteEndObject(); return; }
        var type = definition.GetProperty("type").GetString()!;
        writer.WriteStartObject();
        writer.WriteString("type", type);
        if (definition.TryGetProperty("description", out var description)) writer.WriteString("description", description.GetString());

        if (type == "object")
        {
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            if (definition.TryGetProperty("properties", out var properties)) foreach (var property in properties.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                WriteNormalizedSchema(writer, property.Value);
            }
            writer.WriteEndObject();
            if (definition.TryGetProperty("required", out var required))
            {
                writer.WritePropertyName("required");
                required.WriteTo(writer);
            }
            if (definition.TryGetProperty("additionalProperties", out var additional))
            {
                writer.WritePropertyName("additionalProperties");
                if (additional.ValueKind == JsonValueKind.Object) WriteNormalizedSchema(writer, additional);
                else additional.WriteTo(writer);
            }
            else writer.WriteBoolean("additionalProperties", false);
            if (definition.TryGetProperty("propertyNames", out var propertyNames))
            {
                writer.WritePropertyName("propertyNames");
                propertyNames.WriteTo(writer);
            }
        }

        if (type == "array")
        {
            writer.WritePropertyName("items");
            WriteNormalizedSchema(writer, definition.GetProperty("items"));
            WriteIntegerBound(writer, definition, "minItems", defaultValue: 0);
            WriteIntegerBound(writer, definition, "maxItems", MaximumArrayItems);
        }

        if (type == "string")
        {
            WriteEnum(writer, definition);
            WriteIntegerBound(writer, definition, "minLength", defaultValue: 0);
            WriteIntegerBound(writer, definition, "maxLength", MaximumStringLength);
        }

        if (type is "integer" or "number")
        {
            WriteEnum(writer, definition);
            WriteBound(writer, definition, "minimum");
            WriteBound(writer, definition, "maximum");
        }

        if (type == "boolean") WriteEnum(writer, definition);
        writer.WriteEndObject();
    }

    private static void ValidateValue(JsonElement definition, JsonElement value, int depth, ref int visitedValues)
    {
        visitedValues++;
        if (depth > MaximumDepth || visitedValues > MaximumValueCount || !MatchesType(definition.GetProperty("type").GetString()!, value))
            throw InvalidArguments();

        var type = definition.GetProperty("type").GetString()!;
        if (definition.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(candidate => SameScalar(candidate, value)))
            throw InvalidArguments();

        switch (type)
        {
            case "object":
                ValidateObjectValue(definition, value, depth, ref visitedValues);
                break;
            case "array":
                ValidateArrayValue(definition, value, depth, ref visitedValues);
                break;
            case "string":
                var text = value.GetString()!;
                ValidateLength(definition, text.Length, "minLength", "maxLength");
                break;
            case "integer":
            case "number":
                ValidateNumericValue(definition, value, type);
                break;
        }
    }

    private static void ValidateObjectValue(JsonElement definition, JsonElement value, int depth, ref int visitedValues)
    {
        var properties = definition.TryGetProperty("properties", out var declared) ? declared : default;
        var hasAdditional = definition.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object;
        var openMap = hasAdditional && IsAnySchema(additional);
        if (value.EnumerateObject().Count() > (openMap ? MaximumObjectProperties : MaximumProperties)) throw InvalidArguments();
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!supplied.Add(field.Name)) throw InvalidArguments();
            if (definition.TryGetProperty("propertyNames", out var nameSchema)) ValidateStringValue(nameSchema, field.Name);
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(field.Name, out var fieldSchema))
                ValidateValue(fieldSchema, field.Value, depth + 1, ref visitedValues);
            else if (openMap) ValidateAnyValue(field.Value, depth + 1, ref visitedValues);
            else throw InvalidArguments();
        }

        if (definition.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(field => !supplied.Contains(field.GetString()!)))
            throw InvalidArguments();
    }

    private static void ValidateStringValue(JsonElement schema, string value)
    {
        if (value.Length > ReadIntegerBound(schema, "maxLength", MaximumStringLength) || value.Length < ReadIntegerBound(schema, "minLength", 0))
            throw InvalidArguments();
    }

    private static void ValidateAnyValue(JsonElement value, int depth, ref int visitedValues)
    {
        visitedValues++;
        if (depth > MaximumDepth || visitedValues > MaximumValueCount) throw InvalidArguments();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = value.EnumerateObject().ToArray();
                if (fields.Length > MaximumObjectProperties) throw InvalidArguments();
                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 128) throw InvalidArguments();
                    ValidateAnyValue(field.Value, depth + 1, ref visitedValues);
                }
                break;
            case JsonValueKind.Array:
                var items = value.EnumerateArray().ToArray();
                if (items.Length > MaximumArrayItems) throw InvalidArguments();
                foreach (var item in items) ValidateAnyValue(item, depth + 1, ref visitedValues);
                break;
            case JsonValueKind.String:
                if (value.GetString()!.Length > MaximumStringLength) throw InvalidArguments();
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) throw InvalidArguments();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default: throw InvalidArguments();
        }
    }

    private static void ValidateArrayValue(JsonElement definition, JsonElement value, int depth, ref int visitedValues)
    {
        var items = value.EnumerateArray().ToArray();
        ValidateLength(definition, items.Length, "minItems", "maxItems");
        foreach (var item in items) ValidateValue(definition.GetProperty("items"), item, depth + 1, ref visitedValues);
    }

    private static object? ConvertValue(JsonElement definition, JsonElement value)
    {
        var type = definition.GetProperty("type").GetString()!;
        return type switch
        {
            "object" => value.EnumerateObject().ToDictionary(field => field.Name,
                field => definition.TryGetProperty("properties", out var properties) && properties.TryGetProperty(field.Name, out var propertySchema)
                    ? ConvertValue(propertySchema, field.Value) : ConvertAnyValue(field.Value), StringComparer.Ordinal),
            "array" => value.EnumerateArray().Select(item => ConvertValue(definition.GetProperty("items"), item)).ToArray(),
            "string" => value.GetString()!,
            "integer" => value.GetInt64(),
            "number" => value.GetDouble(),
            "boolean" => value.GetBoolean(),
            _ => throw InvalidArguments()
        };
    }

    private static object? ConvertAnyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(field => field.Name, field => ConvertAnyValue(field.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ConvertAnyValue).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw InvalidArguments()
    };

    private static bool MatchesType(string type, JsonElement value) => (type, value.ValueKind) switch
    {
        ("object", JsonValueKind.Object) => true,
        ("array", JsonValueKind.Array) => true,
        ("string", JsonValueKind.String) => true,
        ("integer", JsonValueKind.Number) => value.TryGetInt64(out _),
        ("number", JsonValueKind.Number) => value.TryGetDouble(out var number) && double.IsFinite(number),
        ("boolean", JsonValueKind.True or JsonValueKind.False) => true,
        _ => false
    };

    private static void ValidateEnum(JsonElement definition, string type)
    {
        if (!definition.TryGetProperty("enum", out var values)) return;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 64 ||
            values.EnumerateArray().Any(value => !MatchesType(type, value) || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
            throw UnsupportedSchema();
        if (values.EnumerateArray().Select(value => value.GetRawText()).Distinct(StringComparer.Ordinal).Count() != values.GetArrayLength())
            throw UnsupportedSchema();
    }

    private static void ValidateDescription(JsonElement definition)
    {
        if (definition.TryGetProperty("description", out var description) &&
            (description.ValueKind != JsonValueKind.String || description.GetString()!.Length > 1_024))
            throw UnsupportedSchema();
    }

    private static void ValidateArrayBounds(JsonElement definition)
    {
        var minimum = ReadIntegerBound(definition, "minItems", defaultValue: 0);
        var maximum = ReadIntegerBound(definition, "maxItems", defaultValue: MaximumArrayItems);
        if (minimum < 0 || maximum > MaximumArrayItems || minimum > maximum) throw UnsupportedSchema();
    }

    private static void ValidateStringBounds(JsonElement definition)
    {
        var minimum = ReadIntegerBound(definition, "minLength", defaultValue: 0);
        var maximum = ReadIntegerBound(definition, "maxLength", defaultValue: MaximumStringLength);
        if (minimum < 0 || maximum > MaximumStringLength || minimum > maximum) throw UnsupportedSchema();
    }

    private static int ReadIntegerBound(JsonElement definition, string name, int defaultValue)
    {
        if (!definition.TryGetProperty(name, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var bound)) throw UnsupportedSchema();
        return bound;
    }

    private static void ValidateNumberBounds(JsonElement definition, string type)
    {
        var minimum = ReadNumberBound(definition, "minimum");
        var maximum = ReadNumberBound(definition, "maximum");
        if (minimum is { } lower && maximum is { } upper && lower > upper) throw UnsupportedSchema();
        if (type == "integer" && (minimum is { } min && min != Math.Truncate(min) || maximum is { } max && max != Math.Truncate(max)))
            throw UnsupportedSchema();
    }

    private static double? ReadNumberBound(JsonElement definition, string name)
    {
        if (!definition.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var bound) || !double.IsFinite(bound))
            throw UnsupportedSchema();
        return bound;
    }

    private static void ValidateNumericValue(JsonElement definition, JsonElement value, string type)
    {
        if (type == "integer" && !value.TryGetInt64(out _)) throw InvalidArguments();
        var number = value.GetDouble();
        var minimum = ReadNumberBound(definition, "minimum");
        var maximum = ReadNumberBound(definition, "maximum");
        if (minimum is { } lower && number < lower || maximum is { } upper && number > upper) throw InvalidArguments();
    }

    private static void ValidateLength(JsonElement definition, int actual, string minimumName, string maximumName)
    {
        var maximumDefault = maximumName == "maxItems" ? MaximumArrayItems : MaximumStringLength;
        var minimum = ReadIntegerBound(definition, minimumName, 0);
        var maximum = ReadIntegerBound(definition, maximumName, maximumDefault);
        if (actual < minimum || actual > maximum) throw InvalidArguments();
    }

    private static bool SameScalar(JsonElement left, JsonElement right) => (left.ValueKind, right.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) => left.GetString() == right.GetString(),
        (JsonValueKind.Number, JsonValueKind.Number) => left.GetDouble() == right.GetDouble(),
        (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) => true,
        _ => false
    };

    private static void WriteEnum(Utf8JsonWriter writer, JsonElement definition)
    {
        if (!definition.TryGetProperty("enum", out var values)) return;
        writer.WritePropertyName("enum");
        values.WriteTo(writer);
    }

    private static void WriteBound(Utf8JsonWriter writer, JsonElement definition, string name)
    {
        if (!definition.TryGetProperty(name, out var value)) return;
        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }

    private static void WriteIntegerBound(Utf8JsonWriter writer, JsonElement definition, string name, int defaultValue)
    {
        writer.WriteNumber(name, ReadIntegerBound(definition, name, defaultValue));
    }

    private static ToolInvocationException UnsupportedSchema() =>
        new("This MCP tool uses an unsupported input schema. Supported inputs are bounded nested objects and arrays with string, integer, number, and boolean values.");

    private static ToolInvocationException InvalidArguments() =>
        new("Tool arguments do not match the discovered input schema or supported size limits.");
}
