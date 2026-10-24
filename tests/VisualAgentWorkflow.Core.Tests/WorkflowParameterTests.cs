using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowParameterTests
{
    private static WorkflowDefinition Definition() => new WorkflowDefinition("1.6", "parameter-flow", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["value"] = new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "greeting" },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("value", "in"), "data"),
            ["value-output"] = new(new("value", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary())
    {
        Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            .Add("greeting", new("Greeting", "Hello"))
    };

    [Fact]
    public void DefinitionRoundTripsParameterSchemaAndNodeReference()
    {
        var serialized = WorkflowJson.Serialize(Definition());
        var restored = WorkflowJson.Deserialize(serialized);

        Assert.Equal("Hello", restored.Parameters!["greeting"].DefaultValue);
        Assert.Equal("greeting", restored.Nodes["value"].ParameterId);
        Assert.Equal(WorkflowJson.ComputeHash(Definition()), WorkflowJson.ComputeHash(restored));
    }

    [Fact]
    public void ParameterNodeMustReferenceDeclaredParameter()
    {
        var invalid = Definition() with { Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "parameter.reference");
    }

    [Fact]
    public void ParameterNodesCanBeInsertedOnLinearAndConditionalGraphPaths()
    {
        var source = new WorkflowDefinition("1.6", "parameter-edit", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("edge", new(new("input", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("greeting", new("Greeting", "hello"))
        };

        var inserted = PipelineEditing.InsertNodeAfter(source, "input",
            new NodeDefinition(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "greeting" });

        Assert.Equal("1.6", inserted.SchemaVersion);
        Assert.Contains(inserted.Nodes.Values, node => node.Type == NodeTypes.WorkflowValue && node.ParameterId == "greeting");
        WorkflowValidator.ThrowIfInvalid(inserted);
        Assert.Equal(3, inserted.Nodes.Count);
    }

    [Fact]
    public void RemovingReferencedParameterIsRejectedAndUnreferencedParameterCanBeRemoved()
    {
        Assert.Throws<ArgumentException>(() => WorkflowParameterEditing.Remove(Definition(), "greeting"));
        var unbound = Definition() with { Nodes = Definition().Nodes.SetItem("value", new(NodeTypes.Uppercase, "1.0.0")) };
        var without = WorkflowParameterEditing.Remove(unbound, "greeting");

        Assert.Empty(without.Parameters!);
    }

    [Fact]
    public void ReusableInsertionCopiesAndRemapsParameterIdsWhenDefaultsConflict()
    {
        var source = Definition() with { WorkflowId = "reusable" };
        var sourceRevision = WorkflowRevision.Create(source, "author");
        var target = new WorkflowDefinition("1.6", "consumer", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("step", new(NodeTypes.Uppercase, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("a", new(new("input", "out"), new("step", "in"), "data"))
                .Add("b", new(new("step", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("greeting", new("Greeting", "consumer default"))
        };
        var composition = PipelineReuse.Insert(target, WorkflowLayout.Create(target), "input",
            new(1, sourceRevision), WorkflowLayout.Create(source));
        var copiedNode = composition.Definition.Nodes.Values.Single(node => node.Type == NodeTypes.WorkflowValue);

        Assert.NotEqual("greeting", copiedNode.ParameterId);
        Assert.Equal("Hello", composition.Definition.Parameters![copiedNode.ParameterId!].DefaultValue);
        WorkflowValidator.ThrowIfInvalid(composition.Definition);
    }

    [Fact]
    public void OverridesAreBoundedAndCannotIntroduceUndeclaredValues()
    {
        Assert.Equal("Custom", WorkflowParameters.Resolve(Definition(), new Dictionary<string, string> { ["greeting"] = "Custom" })!["greeting"]);
        Assert.Throws<ArgumentException>(() => WorkflowParameters.Resolve(Definition(), new Dictionary<string, string> { ["unknown"] = "x" }));
        Assert.Throws<ArgumentException>(() => WorkflowParameters.Resolve(Definition(), new Dictionary<string, string> { ["greeting"] = new string('x', 16_385) }));
    }

    [Fact]
    public void LegacyDefinitionDoesNotGainOptionalParameterJson()
    {
        var legacy = new WorkflowDefinition("1.0", "legacy", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("edge", new(new("input", "out"), new("output", "in"), "data")));

        Assert.DoesNotContain("parameters", WorkflowJson.Serialize(legacy), StringComparison.Ordinal);
    }
}
