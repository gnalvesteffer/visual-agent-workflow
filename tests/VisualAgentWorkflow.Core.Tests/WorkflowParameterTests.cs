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
    public void OptionalDescriptionRoundTripsAndIsEditedWithoutChangingDefault()
    {
        var described = WorkflowParameterEditing.UpdateDescription(Definition(), "greeting", "Shown to the customer.");
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(described));

        Assert.Equal("Shown to the customer.", restored.Parameters!["greeting"].Description);
        var renamed = WorkflowParameterEditing.Update(restored, "greeting", "Welcome text", "Hello");
        Assert.Equal("Shown to the customer.", renamed.Parameters!["greeting"].Description);
        Assert.Equal("Hello", renamed.Parameters["greeting"].DefaultValue);
        Assert.Equal("1.6", renamed.SchemaVersion);
    }

    [Fact]
    public void ParameterDescriptionIsBoundedAndNullIsOmitted()
    {
        Assert.DoesNotContain("description", WorkflowJson.Serialize(Definition()), StringComparison.Ordinal);
        var invalid = Definition() with
        {
            Parameters = Definition().Parameters!.SetItem("greeting", new WorkflowParameterDefinition("Greeting", "Hello")
            { Description = new string('x', 513) })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "parameter.definition");
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

        var inserted = WorkflowEditing.InsertNodeAfter(source, "input",
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
        var source = Definition() with
        {
            WorkflowId = "reusable",
            Parameters = Definition().Parameters!.SetItem("greeting", new WorkflowParameterDefinition("Greeting", "Hello")
            { Description = "A greeting used by this workflow." })
        };
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
        var composition = WorkflowReuse.Insert(target, WorkflowLayout.Create(target), "input",
            new(1, sourceRevision), WorkflowLayout.Create(source));
        var copiedNode = composition.Definition.Nodes.Values.Single(node => node.Type == NodeTypes.WorkflowValue);

        Assert.NotEqual("greeting", copiedNode.ParameterId);
        Assert.Equal("Hello", composition.Definition.Parameters![copiedNode.ParameterId!].DefaultValue);
        Assert.Equal("A greeting used by this workflow.", composition.Definition.Parameters[copiedNode.ParameterId!].Description);
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
    public void NumberPropagationUsesInvariantFiniteDoublesAndRejectsNonFiniteText()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.NumberPropagationGraph, "numbers", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("edge", new(new("input", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("amount", new WorkflowParameterDefinition("Amount", "1.25e1") { Type = "number" })
        };

        Assert.Equal("12.5", WorkflowParameters.Resolve(definition, null)!["amount"]);
        Assert.True(WorkflowParameters.TryParseFiniteNumber("-0", out var negativeZero));
        Assert.True(double.IsNegative(negativeZero));
        Assert.False(WorkflowParameters.TryParseFiniteNumber("NaN", out _));
        Assert.False(WorkflowParameters.TryParseFiniteNumber("Infinity", out _));
        Assert.False(WorkflowParameters.TryParseFiniteNumber("1,25", out _));
        Assert.Throws<ArgumentException>(() => WorkflowParameters.Resolve(definition, new Dictionary<string, string> { ["amount"] = "1e999" }));
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
