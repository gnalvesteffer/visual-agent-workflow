using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Characterizes how the Core reader treats content it does not recognize inside a schema 2.0 definition: an unknown
/// optional node property and an unknown node type (Decision 0082, P3 and P5). Measured before the shaped-json slice
/// existed, with <c>futureAnnotation</c> and <c>core.future.unknown</c> as the unknown names; the same behavior is pinned here
/// with names that stay unknown. The result: both fail closed. Nothing is silently dropped, so no schema or
/// capability gate is required for the shaped-json annotation.
/// </summary>
public sealed class UnknownNodeAnnotationTests
{
    private static string Fixture(string inputExtra = "", string extraNodes = "") => """
        {"schemaVersion":"2.0","workflowId":"fixture","entryNodeId":"input",
         "nodes":{"input":{"type":"core.input","typeVersion":"1.0.0"@INPUT@},"output":{"type":"core.output","typeVersion":"1.0.0"}@NODES@},
         "connections":{"value":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}},
         "executionConnections":{"finish":{"source":{"nodeId":"input","portId":"then"},"target":{"nodeId":"output","portId":"execute"},"kind":"execution"}}}
        """.Replace("@INPUT@", inputExtra, StringComparison.Ordinal).Replace("@NODES@", extraNodes, StringComparison.Ordinal);

    [Fact]
    public void Baseline_fixture_is_valid()
    {
        var definition = WorkflowJson.Deserialize(Fixture());
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Unknown_node_property_is_rejected_by_deserialize_not_dropped()
    {
        var json = Fixture(inputExtra: ",\"futureAnnotation\":{\"fields\":[]}");
        Assert.Throws<JsonException>(() => WorkflowJson.Deserialize(json));
        Assert.Throws<JsonException>(() => WorkflowJson.DeserializeDraft(json));
    }

    [Fact]
    public void Unknown_nested_property_inside_a_known_annotation_is_rejected()
    {
        var json = Fixture(inputExtra: ",\"inputDataType\":\"json\",\"wait\":{\"durationMilliseconds\":1,\"futureOption\":true}");
        Assert.Throws<JsonException>(() => WorkflowJson.DeserializeDraft(json));
    }

    [Fact]
    public void Unknown_node_type_is_rejected_by_validation_with_a_node_type_issue()
    {
        var json = Fixture(extraNodes: ",\"future\":{\"type\":\"core.future.unknown\",\"typeVersion\":\"1.0.0\"}");
        Assert.ThrowsAny<Exception>(() => WorkflowJson.Deserialize(json));
        // Draft reading validates too and refuses a node whose type it cannot place.
        Assert.ThrowsAny<Exception>(() => WorkflowJson.DeserializeDraft(json));
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Path.Contains("future", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_node_type_is_not_a_supported_contract()
    {
        Assert.False(NodeTypes.IsSupported("core.future.unknown"));
    }
}
