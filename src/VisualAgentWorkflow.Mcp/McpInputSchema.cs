using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Validated portable MCP input shape supported by the agent adapter.</summary>
public static class McpToolInputSchemas
{
    /// <summary>Returns whether an MCP tool uses the supported flat scalar object schema.</summary>
    public static bool IsSupported(JsonElement schema) => McpInputSchema.IsSupported(schema);

    /// <summary>Returns a normalized schema suitable for an agent function declaration.</summary>
    public static JsonElement Normalize(JsonElement schema) => McpInputSchema.Normalize(schema);
}

internal sealed class McpInputSchema
{
    private static readonly HashSet<string> SupportedTypes = ["string", "integer", "number", "boolean"];
    private readonly IReadOnlyDictionary<string, string> propertyTypes;
    private readonly IReadOnlySet<string> required;

    private McpInputSchema(IReadOnlyDictionary<string, string> propertyTypes, IReadOnlySet<string> required)
    {
        this.propertyTypes = propertyTypes;
        this.required = required;
    }

    internal static McpInputSchema Validate(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var objectType) ||
            objectType.ValueKind != JsonValueKind.String || objectType.GetString() != "object" || !schema.TryGetProperty("properties", out var properties) ||
            schema.GetRawText().Length > 16_384 || properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() > 32)
            throw UnsupportedSchema();

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128 || property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !SupportedTypes.Contains(type.GetString()!) ||
                property.Value.TryGetProperty("description", out var description) &&
                    (description.ValueKind != JsonValueKind.String || description.GetString()!.Length > 1024) ||
                property.Value.EnumerateObject().Any(field => field.Name is not ("type" or "description" or "title")))
                throw UnsupportedSchema();
            types.Add(property.Name, type.GetString()!);
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredFields))
        {
            if (requiredFields.ValueKind != JsonValueKind.Array) throw UnsupportedSchema();
            foreach (var field in requiredFields.EnumerateArray())
                if (field.ValueKind != JsonValueKind.String || !types.ContainsKey(field.GetString()!) || !required.Add(field.GetString()!))
                    throw UnsupportedSchema();
        }

        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
            throw UnsupportedSchema();
        if (schema.EnumerateObject().Any(field => field.Name is not ("type" or "properties" or "required" or "additionalProperties" or "$schema" or "title" or "description")))
            throw UnsupportedSchema();

        return new(types, required);
    }

    internal static JsonElement Normalize(JsonElement schema)
    {
        Validate(schema);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                writer.WriteStartObject();
                writer.WriteString("type", property.Value.GetProperty("type").GetString());
                if (property.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                    writer.WriteString("description", description.GetString());
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            if (schema.TryGetProperty("required", out var required))
            {
                writer.WritePropertyName("required");
                required.WriteTo(writer);
            }
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }
        using var normalized = JsonDocument.Parse(output.ToArray());
        return normalized.RootElement.Clone();
    }

    internal static bool IsSupported(JsonElement schema)
    {
        try { Validate(schema); return true; }
        catch (ToolInvocationException) { return false; }
    }

    public IReadOnlyDictionary<string, object?> ToCallArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ToolInvocationException("Tool arguments must be an object.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!values.TryAdd(property.Name, property.Value)) throw new ToolInvocationException("Tool arguments contain duplicate fields.");

        if (values.Keys.Any(name => !propertyTypes.ContainsKey(name)) || required.Any(name => !values.ContainsKey(name)))
            throw new ToolInvocationException("Tool arguments do not match the discovered input schema.");

        return values.ToDictionary(pair => pair.Key, pair => (object?)ConvertValue(propertyTypes[pair.Key], pair.Value), StringComparer.Ordinal);
    }

    private static object ConvertValue(string type, JsonElement value) => (type, value.ValueKind) switch
    {
        ("string", JsonValueKind.String) => value.GetString()!,
        ("integer", JsonValueKind.Number) when value.TryGetInt64(out var integer) => integer,
        ("number", JsonValueKind.Number) when value.TryGetDouble(out var number) => number,
        ("boolean", JsonValueKind.True) => true,
        ("boolean", JsonValueKind.False) => false,
        _ => throw new ToolInvocationException("A tool argument has the wrong JSON type.")
    };

    private static ToolInvocationException UnsupportedSchema() =>
        new("This MCP tool uses an unsupported input schema. Supported inputs are flat object fields of type string, integer, number, or boolean.");
}
