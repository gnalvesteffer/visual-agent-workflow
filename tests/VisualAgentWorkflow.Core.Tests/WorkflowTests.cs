using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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

    private static WorkflowDefinition ExplicitDataAndExecutionGraph() => new WorkflowDefinition(
        "1.23", "explicit-graph", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("concat", "left"), "data"),
            ["suffix"] = new(new("constant", "out"), new("concat", "right"), "data"),
            ["result"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

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
    public void Explicit_execution_and_data_channels_round_trip_without_conflating_their_edges()
    {
        var definition = ExplicitDataAndExecutionGraph();

        var json = WorkflowJson.Serialize(definition);
        var restored = WorkflowJson.Deserialize(json);

        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal("data", Assert.Single(restored.Connections.Values, edge => edge.Target.NodeId == "output").Kind);
        Assert.Equal("execution", Assert.Single(restored.ExecutionConnections!).Value.Kind);
        Assert.Equal(json, WorkflowJson.Serialize(restored));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored));
        Assert.Collection(WorkflowPorts.Explicit(restored.Nodes["output"]),
            port => Assert.Equal(new WorkflowPortDescriptor("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, "string"), port),
            port => Assert.Equal(new WorkflowPortDescriptor("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution), port));
    }

    [Fact]
    public void Explicit_data_edges_do_not_satisfy_execution_activation_requirements()
    {
        var definition = ExplicitDataAndExecutionGraph() with { ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty };

        var issues = WorkflowValidator.Validate(definition);

        Assert.Contains(issues, issue => issue.Code == "execution.degree");
        Assert.Contains(issues, issue => issue.Code == "execution.path");
    }

    [Fact]
    public void Explicit_data_cycles_are_rejected_even_when_the_execution_path_is_valid()
    {
        var definition = ExplicitDataAndExecutionGraph() with
        {
            Connections = ExplicitDataAndExecutionGraph().Connections
                .SetItem("cycle-back", new(new("concat", "out"), new("concat", "left"), "data"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "data.cycle");
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
    public void Workflow_names_are_portable_and_preserve_the_stable_id()
    {
        var original = Pipeline();
        var named = WorkflowMetadataEditing.Rename(original, "  Customer support  ");
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(named));

        Assert.Equal(original.WorkflowId, named.WorkflowId);
        Assert.Equal("Customer support", restored.DisplayName);
        Assert.Equal(WorkflowJson.ComputeHash(named), WorkflowJson.ComputeHash(restored));
        Assert.NotEqual(WorkflowJson.ComputeHash(original), WorkflowJson.ComputeHash(named));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("workflow\nname")]
    public void Workflow_name_rejects_empty_or_control_characters(string displayName) =>
        Assert.Throws<ArgumentException>(() => WorkflowMetadataEditing.Rename(Pipeline(), displayName));

    [Fact]
    public void Workflow_name_rejects_values_over_the_supported_length() =>
        Assert.Throws<ArgumentException>(() => WorkflowMetadataEditing.Rename(Pipeline(), new string('x', 129)));

    [Fact]
    public void RehydratesHistoricalWindowsLineEndingHashesWithoutChangingTheirIdentity()
    {
        var revision = WorkflowRevision.Create(Pipeline(), "alice");
        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            WorkflowJson.Serialize(revision.Definition).Replace("\n", "\r\n", StringComparison.Ordinal))));

        Assert.NotEqual(revision.ContentHash, legacyHash);
        Assert.True(WorkflowJson.MatchesHash(revision.Definition, legacyHash));
        var restored = WorkflowRevision.Rehydrate(revision.RevisionId, revision.Definition, revision.Author,
            revision.ParentRevisionId, revision.CreatedAt, legacyHash);
        Assert.Equal(legacyHash, restored.ContentHash);
        Assert.False(WorkflowJson.MatchesHash(revision.Definition, new string('0', 64)));
    }

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
    [InlineData("core.future-agent")]
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
