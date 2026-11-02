using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphFunctionExtractionTests
{
    [Fact]
    public void Prepares_function_snapshot_and_replaces_region_preserving_fanout_layout_and_execution()
    {
        var source = Caller();
        var layout = WorkflowLayout.Create(source) with
        {
            Nodes = WorkflowLayout.Create(source).Nodes
                .SetItem("combine", new(new(520, 300), "Combine", "Retain this note", true))
                .SetItem("input", new(new(120, 300)))
                .SetItem("sink", new(new(900, 300)))
        };
        layout = layout with
        {
            Sections = ImmutableSortedDictionary<string, GraphSection>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("selected-region", new("Reusable region", ["combine"]))
        };

        var plan = GraphFunctionExtraction.Prepare(source, layout, ["combine"], "text.combine", 1,
            "Combine", "Repeats its input.", "call-combine", prefix => "new-" + prefix);

        Assert.Empty(GraphFunctionJson.Validate(plan.Function.Definition, allowNumber: true, allowNumberAdd: true));
        Assert.Equal(GraphFunctionJson.ComputeHash(plan.Function.Definition), plan.Function.ContentHash);
        Assert.Equal(new[] { "input-1" }, plan.Function.Definition.Inputs.Keys);
        Assert.Equal(new[] { "output-1" }, plan.Function.Definition.Outputs.Keys);
        Assert.Contains(plan.Function.Definition.Connections.Values,
            edge => edge.Source.NodeId.StartsWith("function-input-input-1", StringComparison.Ordinal) && edge.Target == new PortReference("combine", "left"));
        Assert.Contains(plan.Function.Definition.Connections.Values,
            edge => edge.Source.NodeId.StartsWith("function-input-input-1", StringComparison.Ordinal) && edge.Target == new PortReference("combine", "right"));
        Assert.Equal("Retain this note", plan.FunctionLayout.Nodes["combine"].Comment);
        Assert.Equal("Combine", plan.FunctionLayout.Nodes["combine"].Label);
        Assert.Contains(plan.FunctionLayout.Sections.Values, section => section.Label == "Reusable region" && section.NodeIds.Contains("combine"));

        Assert.DoesNotContain("combine", plan.Definition.Nodes.Keys);
        Assert.Equal(plan.Function, plan.Definition.Nodes["call-combine"].GraphFunction);
        Assert.Equal(2, plan.Definition.Connections.Values.Count(edge => edge.Source.NodeId == "call-combine"));
        Assert.Contains(plan.Definition.Connections.Values,
            edge => edge.Source == new PortReference("input", "out") && edge.Target == new PortReference("call-combine", "input-1"));
        Assert.Contains(plan.Definition.ExecutionConnections!.Values,
            edge => edge.Source == new PortReference("input", "then") && edge.Target == new PortReference("output", "execute"));
        Assert.Equal("Reusable region", Assert.Single(plan.Layout.Sections.Values).Label);
        Assert.Contains("call-combine", Assert.Single(plan.Layout.Sections.Values).NodeIds);
        Assert.Empty(WorkflowValidator.Validate(plan.Definition));
    }

    [Fact]
    public void Rejects_regions_without_external_input_or_output_boundaries()
    {
        var source = Caller();

        var constantSource = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "constant-only", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("fixed") },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["constant-output"] = new(new("constant", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("activate-output", new(new("input", "then"), new("output", "execute"), "execution"))
        };
        var noOutputSource = source with
        {
            Nodes = source.Nodes.Add("orphan", new(NodeTypes.TextConcat, "1.0.0")),
            Connections = source.Connections.Add("orphan-left", new(new("input", "out"), new("orphan", "left"), "data"))
                .Add("orphan-right", new(new("input", "out"), new("orphan", "right"), "data"))
        };
        var noInput = Assert.Throws<ArgumentException>(() => GraphFunctionExtraction.Prepare(constantSource,
            WorkflowLayout.Create(constantSource), ["constant"], "text.constant", 1, "Constant", "", "constant-call"));
        var noOutput = Assert.Throws<ArgumentException>(() => GraphFunctionExtraction.Prepare(noOutputSource,
            WorkflowLayout.Create(noOutputSource), ["orphan"], "text.orphan", 1, "Orphan", "", "orphan-call"));

        Assert.Contains("at least one connected input and one connected output", noInput.Message, StringComparison.Ordinal);
        Assert.Contains("at least one connected input and one connected output", noOutput.Message, StringComparison.Ordinal);
        Assert.Contains("combine", source.Nodes.Keys);
    }

    [Fact]
    public void Rejects_effectful_selection_and_generated_connection_id_collision()
    {
        var source = Caller();
        var effectful = source with
        {
            Nodes = source.Nodes.SetItem("agent", new(NodeTypes.Agent, "1.0.0"))
        };
        var effectError = Assert.Throws<ArgumentException>(() => GraphFunctionExtraction.Prepare(effectful,
            WorkflowLayout.Create(effectful), ["agent"], "bad.function", 1, "Bad", "", "bad-call"));

        var collisionError = Assert.Throws<ArgumentException>(() => GraphFunctionExtraction.Prepare(source,
            WorkflowLayout.Create(source), ["combine"], "text.combine", 1, "Combine", "", "call-combine", _ => "input"));

        Assert.Contains("not a supported stateless pure data node", effectError.Message, StringComparison.Ordinal);
        Assert.Contains("invalid or colliding", collisionError.Message, StringComparison.Ordinal);
    }

    private static WorkflowDefinition Caller() => new(
        WorkflowSchemaVersions.UnifiedWorkflowGraph,
        "function-extraction",
        "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["combine"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["sink"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["combine-left"] = new(new("input", "out"), new("combine", "left"), "data"),
            ["combine-right"] = new(new("input", "out"), new("combine", "right"), "data"),
            ["sink-left"] = new(new("combine", "out"), new("sink", "left"), "data"),
            ["sink-right"] = new(new("combine", "out"), new("sink", "right"), "data"),
            ["output"] = new(new("sink", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };
}
