using System.Text.Json;

const string protocolVersion = "2025-11-25";
const string inputSchema = """
    {"type":"object","properties":{"timestamp":{"type":"string","format":"date-time"}},"required":["timestamp"]}
    """;

string? line;
while ((line = Console.ReadLine()) is not null)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    if (!root.TryGetProperty("id", out var id)) continue;

    var method = root.GetProperty("method").GetString();
    if (method == "initialize")
    {
        WriteResponse(id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", protocolVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", "invalid-schema-fixture");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }
    else if (method == "tools/list")
    {
        WriteResponse(id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("name", "invalid_schema");
            writer.WriteString("description", "Fixture tool with a schema keyword unsupported by the broker.");
            writer.WritePropertyName("inputSchema");
            using var schema = JsonDocument.Parse(inputSchema);
            schema.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
    else if (method == "ping")
    {
        WriteResponse(id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
    }
    else
    {
        WriteError(id, -32601, "Method not found.");
    }
}

static void WriteResponse(JsonElement id, Action<Utf8JsonWriter> writeResult)
{
    using var output = new MemoryStream();
    using (var writer = new Utf8JsonWriter(output))
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("result");
        writeResult(writer);
        writer.WriteEndObject();
    }
    Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
    Console.Out.Flush();
}

static void WriteError(JsonElement id, int code, string message)
{
    using var output = new MemoryStream();
    using (var writer = new Utf8JsonWriter(output))
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
    Console.Out.Flush();
}
