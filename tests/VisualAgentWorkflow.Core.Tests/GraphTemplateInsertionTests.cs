using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphTemplateInsertionTests
{
    [Fact]
    public void Inserts_pure_template_and_replaces_one_existing_target_input_edge()
    {
        var result = GraphTemplateEditing.Insert(
            Target(),
            Template(),
            new GraphTemplateInsertionRequest(
                [new("query", new PortReference("input", "out"))],
                [new("answer", new PortReference("output", "in"))]),
            source => "copy-" + source,
            source => "copy-edge-" + source);

        Assert.Empty(WorkflowValidator.Validate(result.Definition));
        Assert.Equal(2, result.AddedNodeIds.Length);
        Assert.Equal(3, result.AddedConnectionIds.Length);
        Assert.Single(result.RemovedConnectionIds);
        Assert.Equal("second", result.RemovedConnectionIds[0]);
        Assert.Equal("copy-concat", result.TemplateNodeMap["concat"]);
        Assert.Equal(3, result.TemplateConnectionMap.Count);
        Assert.All(result.TemplateConnectionMap.Values, value => Assert.StartsWith("copy-edge-", value, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Definition.Connections.Values,
            edge => edge.Target == new PortReference("output", "in") && edge.Source.NodeId == "transform");
        Assert.Contains(result.Definition.Connections.Values,
            edge => edge.Source == new PortReference("input", "out") && edge.Target.NodeId == "copy-concat");
    }

    [Fact]
    public void Rewires_a_bound_input_to_the_copied_template()
    {
        var result = GraphTemplateEditing.Insert(
            Target(),
            Template(),
            new GraphTemplateInsertionRequest(
                [new("query", new PortReference("input", "out"))],
                [new("answer", new PortReference("output", "in"))]),
            source => "fanout-" + source,
            source => "fanout-edge-" + source);

        Assert.Contains(result.Definition.Connections.Values,
            edge => edge.Source == new PortReference("input", "out") && edge.Target.NodeId == "fanout-concat");
    }

    [Fact]
    public void Rejects_missing_bindings_without_mutating_the_input_definition()
    {
        var target = Target();
        var request = new GraphTemplateInsertionRequest([], [new("answer", new PortReference("output", "in"))]);

        var exception = Assert.Throws<ArgumentException>(() => GraphTemplateEditing.Insert(target, Template(), request));

        Assert.Contains("bound exactly once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, target.Nodes.Count);
        Assert.Single(target.Connections);
    }

    [Fact]
    public void Rejects_invalid_generated_connection_ids_before_returning_a_partial_result()
    {
        var request = new GraphTemplateInsertionRequest(
            [new("query", new PortReference("input", "out"))],
            [new("answer", new PortReference("output", "in"))]);

        var exception = Assert.Throws<ArgumentException>(() => GraphTemplateEditing.Insert(
            Target(), Template(), request, source => "node-" + source, _ => "not valid"));

        Assert.Contains("invalid or colliding", exception.Message, StringComparison.Ordinal);
    }

    private static WorkflowDefinition Target() => new(
        "1.23", "target", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["second"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static GraphTemplateDefinition Template()
    {
        var function = new GraphFunctionDefinition(
            "suffix.body", 1, "Suffix", "Adds a suffix.",
            new Dictionary<string, GraphFunctionPort>
            {
                ["query"] = new("Query", WorkflowDataTypes.String)
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, GraphFunctionPort>
            {
                ["answer"] = new("Answer", WorkflowDataTypes.String)
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "query", OutputDataType = WorkflowDataTypes.String },
                ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
                ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
                ["output"] = new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "answer", InputDataType = WorkflowDataTypes.String }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-concat"] = new(new("input", "out"), new("concat", "left"), "data"),
                ["constant-concat"] = new(new("constant", "out"), new("concat", "right"), "data"),
                ["concat-output"] = new(new("concat", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));

        return GraphTemplateExtraction.Extract(function, "suffix", 1, "Suffix", "Adds a suffix.");
    }
}
