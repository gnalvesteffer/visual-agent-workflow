using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class JsonProjectionTests
{
    private static WorkflowDefinition JsonGraph() => new("1.13", "json-project-test", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
            ["project"] = new(NodeTypes.JsonProject, "1.0.0")
            {
                JsonProjection = new([new("person", "/customer/name"), new("amount", "/orders/0/amount")])
            },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["json"] = new(new("input", "out"), new("project", "in"), "data"),
            ["result"] = new(new("project", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Fact]
    public void ProjectionContractRoundTripsAndKeepsJsonPinsTyped()
    {
        var definition = JsonGraph();

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal("json", Assert.Single(WorkflowPorts.Inputs(definition.Nodes["project"])).DataType);
        Assert.Equal("json", Assert.Single(WorkflowPorts.Outputs(definition.Nodes["project"])).DataType);
        Assert.Equal("json", Assert.Single(WorkflowPorts.Inputs(definition.Nodes["output"])).DataType);
        Assert.Equal(WorkflowJson.Serialize(definition), WorkflowJson.Serialize(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition))));

        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition)));
        Assert.Equal(WorkflowJson.ComputeHash(definition), package.DefinitionHash);
    }

    [Fact]
    public void ProjectionRequiresUniqueNamesAndValidPointersInSchema113()
    {
        var definition = JsonGraph();
        var project = definition.Nodes["project"];
        var duplicate = project with { JsonProjection = new([new("value", "/a"), new("value", "/b")]) };
        Assert.Contains(WorkflowValidator.Validate(definition with { Nodes = definition.Nodes.SetItem("project", duplicate) }), issue => issue.Code == "json.projection");

        var invalidPointer = project with { JsonProjection = new([new("value", "/bad~2pointer")]) };
        Assert.Contains(WorkflowValidator.Validate(definition with { Nodes = definition.Nodes.SetItem("project", invalidPointer) }), issue => issue.Code == "json.projection");

        Assert.Contains(WorkflowValidator.Validate(definition with { SchemaVersion = "1.12" }), issue => issue.Code == "node.schema");
    }

    [Fact]
    public void ProjectionEnforcesFieldAndPropertyNameBounds()
    {
        var definition = JsonGraph();
        var project = definition.Nodes["project"];
        var tooManyFields = project with
        {
            JsonProjection = new(Enumerable.Range(0, JsonProjectionLimits.MaximumFields + 1)
                .Select(index => new JsonProjectionField($"field{index}", "/value")).ToImmutableArray())
        };
        Assert.Contains(WorkflowValidator.Validate(definition with { Nodes = definition.Nodes.SetItem("project", tooManyFields) }),
            issue => issue.Code == "json.projection");

        var tooLongName = project with
        {
            JsonProjection = new([new(new string('x', JsonProjectionLimits.MaximumNameCharacters + 1), "/value")])
        };
        Assert.Contains(WorkflowValidator.Validate(definition with { Nodes = definition.Nodes.SetItem("project", tooLongName) }),
            issue => issue.Code == "json.projection");
    }

    [Fact]
    public void InsertingProjectionInfersJsonOutputType()
    {
        var definition = new WorkflowDefinition("1.13", "json-project-insert", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["edge"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());

        var inserted = GraphEditing.InsertNodeAfter(definition, "input", "out",
            new(NodeTypes.JsonProject, "1.0.0") { JsonProjection = new([new("name", "/name")]) });

        Assert.Empty(WorkflowValidator.Validate(inserted));
        Assert.Equal("json", inserted.Nodes["output"].OutputDataType);
        Assert.Contains(inserted.Connections.Values, edge => edge.Source.NodeId == "input" && edge.Target.NodeId != "output");
    }
}
