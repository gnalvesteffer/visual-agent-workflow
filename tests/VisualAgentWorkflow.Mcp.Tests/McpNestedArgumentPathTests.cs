using System.Text.Json;
using VisualAgentWorkflow.Mcp;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class McpNestedArgumentPathTests
{
    [Fact]
    public void ToPointer_EscapesPropertyNamesWithoutAmbiguousSeparators()
    {
        var pointer = McpNestedArgumentPath.ToPointer(["request", "a/b~c"]);

        Assert.Equal("/request/a~1b~0c", pointer);
    }

    [Fact]
    public void ToPinId_IsDeterministicForTheSameCanonicalPath()
    {
        var first = McpNestedArgumentPath.ToPinId(["request", "name"]);
        var samePath = McpNestedArgumentPath.ToPinId(["request", "name"]);

        Assert.Equal(first, samePath);
        Assert.Matches("^arg\\.nested\\.[0-9a-f]{64}$", first);
    }

    [Fact]
    public void ToPinId_ChangesWhenPropertyIdentityChanges()
    {
        var original = McpNestedArgumentPath.ToPinId(["request", "name"]);
        var renamed = McpNestedArgumentPath.ToPinId(["request", "displayName"]);
        var changedCase = McpNestedArgumentPath.ToPinId(["request", "Name"]);

        Assert.NotEqual(original, renamed);
        Assert.NotEqual(original, changedCase);
    }

    [Fact]
    public void ToPointer_DistinguishesDotsInPropertyNamesFromNestedSegments()
    {
        var literalDot = McpNestedArgumentPath.ToPointer(["request.name"]);
        var nestedPath = McpNestedArgumentPath.ToPointer(["request", "name"]);

        Assert.NotEqual(literalDot, nestedPath);
    }

    [Fact]
    public void ToPointer_RejectsRootOnlyPath()
    {
        Assert.Throws<ArgumentException>(() => McpNestedArgumentPath.ToPointer([]));
    }

    [Fact]
    public void TryCreatePins_ProducesStableRequiredAndOptionalLeafPinsAcrossPropertyReordering()
    {
        using var first = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{"request":{"type":"object","additionalProperties":false,"properties":{"name":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["name"]}},"required":["request"]}
            """);
        using var reordered = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{"request":{"type":"object","additionalProperties":false,"properties":{"limit":{"type":"integer","minimum":1,"maximum":10},"name":{"type":"string"}},"required":["name"]}},"required":["request"]}
            """);

        var firstResolved = McpNestedArgumentPath.TryCreatePins(McpToolInputSchemas.Describe(first.RootElement), out var firstPins);
        var reorderedResolved = McpNestedArgumentPath.TryCreatePins(McpToolInputSchemas.Describe(reordered.RootElement), out var reorderedPins);

        Assert.True(firstResolved);
        Assert.True(reorderedResolved);
        Assert.Equal(firstPins.Select(PinIdentity), reorderedPins.Select(PinIdentity));
        Assert.Equal("/request/name", firstPins.Single(pin => pin.Name == "name").Path);
        Assert.True(firstPins.Single(pin => pin.Name == "name").Required);
        Assert.False(firstPins.Single(pin => pin.Name == "limit").Required);
        Assert.Equal(1, firstPins.Single(pin => pin.Name == "limit").Bounds.Minimum);
        Assert.Equal(10, firstPins.Single(pin => pin.Name == "limit").Bounds.Maximum);

        static (string Path, string Id, string Name, string DataType, bool Required, double? Minimum, double? Maximum)
            PinIdentity(McpNestedArgumentPinProof pin) =>
            (pin.Path, pin.Id, pin.Name, pin.DataType, pin.Required, pin.Bounds.Minimum, pin.Bounds.Maximum);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"request\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}}}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"request\":{\"type\":\"object\",\"additionalProperties\":{},\"properties\":{\"name\":{\"type\":\"string\"}}},\"options\":{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}}}}}")]
    public void TryCreatePins_RejectsOpenOrMultipleNestedObjects(string schemaJson)
    {
        using var schema = JsonDocument.Parse(schemaJson);

        Assert.False(McpNestedArgumentPath.TryCreatePins(McpToolInputSchemas.Describe(schema.RootElement), out var pins));
        Assert.Empty(pins);
    }
}
