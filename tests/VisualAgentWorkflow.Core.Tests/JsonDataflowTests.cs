using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class JsonDataflowTests
{
    private static WorkflowDefinition Baseline() => new("1.0", "json-dataflow", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["step"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-step"] = new(new("input", "out"), new("step", "in"), "data"),
            ["step-output"] = new(new("step", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Fact]
    public void JsonSelectorInsertionCreatesTypeCheckedBoundaryAndRemovalRestoresStringGraph()
    {
        var original = Baseline();

        var json = GraphEditing.InsertJsonSelectorAfterInput(original, "input", "/user/name");
        var selectorId = json.Nodes.Single(node => node.Value.Type == NodeTypes.JsonSelectString).Key;

        Assert.Equal("1.9", json.SchemaVersion);
        Assert.Equal("json", WorkflowPorts.Outputs(json.Nodes["input"]).Single().DataType);
        Assert.Equal("json", WorkflowPorts.Inputs(json.Nodes[selectorId]).Single().DataType);
        Assert.Equal("string", WorkflowPorts.Outputs(json.Nodes[selectorId]).Single().DataType);
        Assert.Empty(WorkflowValidator.Validate(json));

        var restored = GraphEditing.RemoveJsonSelector(json, selectorId);
        Assert.Null(restored.Nodes["input"].InputDataType);
        Assert.Equal(original.Connections, restored.Connections);
        Assert.Equal("string", WorkflowPorts.Outputs(restored.Nodes["input"]).Single().DataType);
        Assert.Empty(WorkflowValidator.Validate(restored));
    }

    [Fact]
    public void JsonSelectorMustUseAtomicEntryInsertionAndCannotBeExtractedAsStringPrefab()
    {
        var definition = GraphEditing.InsertJsonSelectorAfterInput(Baseline(), "input", "/user/name");
        var selectorId = definition.Nodes.Single(node => node.Value.Type == NodeTypes.JsonSelectString).Key;

        Assert.Throws<ArgumentException>(() => GraphEditing.InsertNodeAfter(definition, "step", "out",
            new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new("/user/name") }));
        Assert.Throws<ArgumentException>(() => PipelineReuse.Extract(definition, WorkflowLayout.Create(definition),
            new[] { selectorId }, "json-prefab"));
    }

    [Fact]
    public void JsonAndStringPortsCannotBeConnectedDirectly()
    {
        var definition = GraphEditing.InsertJsonSelectorAfterInput(Baseline(), "input", "/user/name");
        var selectorId = definition.Nodes.Single(node => node.Value.Type == NodeTypes.JsonSelectString).Key;
        var edge = definition.Connections.Single(connection => connection.Value.Source.NodeId == "input");
        var invalid = definition with
        {
            Connections = definition.Connections.SetItem(edge.Key,
                edge.Value with { Target = new("step", "in") })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "connection.port");
        Assert.Contains(definition.Connections.Values, connection => connection.Source.NodeId == selectorId);
    }

    [Theory]
    [InlineData("/user/name", "user", "name")]
    [InlineData("/a~1b/~0key", "a/b", "~key")]
    public void JsonPointerDecodesEscapedPathSegments(string pointer, string first, string second)
    {
        var segments = WorkflowJsonPointer.Parse(pointer);

        Assert.Equal(new[] { first, second }, segments);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("/invalid~escape")]
    [InlineData("/invalid~2escape")]
    public void JsonPointerRejectsInvalidSyntax(string pointer) =>
        Assert.Throws<ArgumentException>(() => WorkflowJsonPointer.Parse(pointer));

    [Fact]
    public void JsonPointerEnforcesSegmentAndLengthLimits()
    {
        Assert.Throws<ArgumentException>(() => WorkflowJsonPointer.Parse("/" + string.Join('/', Enumerable.Repeat("x", 17))));
        Assert.Throws<ArgumentException>(() => WorkflowJsonPointer.Parse("/" + new string('x', WorkflowJsonPointer.MaximumLength)));
        Assert.Empty(WorkflowJsonPointer.Parse(""));
    }
}
