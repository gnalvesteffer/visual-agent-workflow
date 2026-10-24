using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class WorkflowTests
{
    private static WorkflowDefinition Pipeline(string transform = NodeTypes.Uppercase) => new(
        "1.0", "example", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["transform"] = new(transform, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["first"] = new(new("input", "out"), new("transform", "in"), "data"),
            ["second"] = new(new("transform", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    [Fact]
    public void ValidPipelineRoundTripsWithStableHash()
    {
        var definition = Pipeline();
        var json = WorkflowJson.Serialize(definition);
        var loaded = WorkflowJson.Deserialize(json);
        Assert.Empty(WorkflowValidator.Validate(loaded));
        Assert.Equal(json, WorkflowJson.Serialize(loaded));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(loaded));
    }

    [Fact]
    public void CanonicalSerializationIgnoresCallerDictionaryOrdering()
    {
        var definition = Pipeline();
        var reversed = definition with
        {
            Nodes = definition.Nodes.WithComparers(Comparer<string>.Create((a, b) => StringComparer.Ordinal.Compare(b, a)))
        };
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(reversed));
    }

    [Fact]
    public void ChangingAnOperationChangesHash() =>
        Assert.NotEqual(WorkflowJson.ComputeHash(Pipeline()), WorkflowJson.ComputeHash(Pipeline(NodeTypes.Reverse)));

    [Fact]
    public void RestoreCreatesNewRevisionWithoutChangingHistory()
    {
        var first = WorkflowRevision.Create(Pipeline(), "alice");
        var second = WorkflowRevision.Create(Pipeline(NodeTypes.Reverse), "bob", first.RevisionId);
        var restored = WorkflowRevision.Restore(first, second, "alice");
        Assert.NotEqual(first.RevisionId, restored.RevisionId);
        Assert.Equal(second.RevisionId, restored.ParentRevisionId);
        Assert.Equal(first.ContentHash, restored.ContentHash);
        Assert.Equal(NodeTypes.Reverse, second.Definition.Nodes["transform"].Type);
    }

    [Fact]
    public void RestoreRejectsAnotherWorkflow()
    {
        var first = WorkflowRevision.Create(Pipeline(), "alice");
        var other = WorkflowRevision.Create(Pipeline() with { WorkflowId = "other" }, "alice");
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Restore(first, other, "alice"));
    }

    [Fact]
    public void MissingEndpointIsReported()
    {
        var definition = Pipeline();
        definition = definition with { Connections = definition.Connections.SetItem("first", new(new("absent", "out"), new("transform", "in"), "data")) };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "connection.reference");
    }

    [Fact]
    public void DisconnectedCycleIsRejected()
    {
        var definition = Pipeline();
        definition = definition with
        {
            Nodes = definition.Nodes.Add("orphan", new(NodeTypes.Reverse, "1.0.0")),
            Connections = definition.Connections.Add("cycle", new(new("orphan", "out"), new("orphan", "in"), "data"))
        };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "graph.unreachable");
    }

    [Theory]
    [InlineData("custom.assembly")]
    [InlineData("core.agent")]
    public void UnknownOrUnimplementedNodesAreRejected(string type) =>
        Assert.Contains(WorkflowValidator.Validate(Pipeline(type)), i => i.Code == "node.unsupported");

    [Fact]
    public void UnsupportedPortsAreRejected()
    {
        var definition = Pipeline();
        definition = definition with { Connections = definition.Connections.SetItem("first", new(new("input", "wrong"), new("transform", "in"), "data")) };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "connection.port");
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejected()
    {
        var json = WorkflowJson.Serialize(Pipeline()).Replace("\"schemaVersion\": \"1.0\",", "\"schemaVersion\": \"2.0\", \"schemaVersion\": \"1.0\",");
        Assert.Throws<JsonException>(() => WorkflowJson.Deserialize(json));
    }

    [Fact]
    public void UnknownJsonFieldsAreRejected()
    {
        var json = WorkflowJson.Serialize(Pipeline()).Replace("\"schemaVersion\":", "\"assembly\": \"unsafe.dll\", \"schemaVersion\":");
        Assert.Throws<JsonException>(() => WorkflowJson.Deserialize(json));
    }

    [Fact]
    public void UnsupportedSchemaAndMissingGraphAreReported()
    {
        var issues = WorkflowValidator.Validate(Pipeline() with { SchemaVersion = "2.0", Nodes = null! });
        Assert.Contains(issues, i => i.Code == "schema.unsupported");
        Assert.Contains(issues, i => i.Code == "graph.missing");
    }
}
