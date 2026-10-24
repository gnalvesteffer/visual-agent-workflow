using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace VisualAgentWorkflow.Mcp;

public static class ToolContractHash
{
    public static string Compute(McpClientTool tool)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WritePropertyName("input"); Canonical(writer, tool.JsonSchema);
            writer.WritePropertyName("output");
            if (tool.ReturnJsonSchema is { } output) Canonical(writer, output); else writer.WriteNullValue();
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) Canonical(writer, element);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
