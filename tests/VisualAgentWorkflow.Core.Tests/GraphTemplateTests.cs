using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphTemplateTests
{
    [Fact]
    public void Pure_template_extracts_and_expands_with_fresh_ids_and_source_mapping()
    {
        var source = new GraphFunctionDefinition(
            "text.suffix", 1, "Suffix", "Adds a suffix.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("text", new("Text", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("result", new("Result", "string")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text" })
                .Add("suffix", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") })
                .Add("concat", new(NodeTypes.TextConcat, "1.0.0"))
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("left", new(new("input", "out"), new("concat", "left"), "data"))
                .Add("right", new(new("suffix", "out"), new("concat", "right"), "data"))
                .Add("result", new(new("concat", "out"), new("output", "in"), "data")));

        var template = GraphTemplateExtraction.Extract(source, "template.text-suffix", 2, "Suffix template", "Pure text suffix template.");
        var expansion = GraphTemplateJson.Expand(template, "expanded.suffix", id => "copy-" + id);

        Assert.Empty(GraphTemplateJson.Validate(template));
        Assert.Equal("expanded.suffix", expansion.Definition.FunctionId);
        Assert.Equal("input", expansion.SourceNodeIds["copy-input"]);
        Assert.Equal("copy-concat", expansion.Definition.Connections["left"].Target.NodeId);
        Assert.Equal(source.Nodes.Count, expansion.Definition.Nodes.Count);
        Assert.Empty(GraphFunctionJson.Validate(expansion.Definition));
        Assert.NotEqual(source.Nodes.Keys, expansion.Definition.Nodes.Keys);
        Assert.Equal(GraphTemplateJson.ComputeHash(template), GraphTemplateJson.ComputeHash(template));
        Assert.NotEqual(GraphTemplateJson.ComputeHash(template), GraphTemplateJson.ComputeHash(template with { Version = 3 }));
    }

    [Fact]
    public void Template_rejects_effectful_nodes_even_if_the_body_is_otherwise_valid()
    {
        var source = new GraphFunctionDefinition(
            "text.identity", 1, "Identity", "Passes text through.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("value", new("Value", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("result", new("Result", "string")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value" })
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new(new("input", "out"), new("output", "in"), "data")));
        var effectful = source with { Nodes = source.Nodes.Add("agent", new(NodeTypes.Agent, "1.0.0")) };

        var issues = GraphTemplateJson.Validate(new("template.identity", 1, "Identity", "", effectful));

        Assert.Contains(issues, issue => issue.Code == "template.function.node.unsupported");
        Assert.Throws<ArgumentException>(() => GraphTemplateExtraction.Extract(effectful, "template.identity", 1, "Identity", ""));
    }

    [Fact]
    public void Expansion_rejects_non_unique_node_ids()
    {
        var source = new GraphFunctionDefinition(
            "text.identity", 1, "Identity", "Passes text through.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("value", new("Value", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("result", new("Result", "string")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value" })
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new(new("input", "out"), new("output", "in"), "data")));
        var template = GraphTemplateExtraction.Extract(source, "template.identity", 1, "Identity", "");

        Assert.Throws<ArgumentException>(() => GraphTemplateJson.Expand(template, "expanded.identity", _ => "same"));
        Assert.Throws<ArgumentException>(() => GraphTemplateJson.Expand(template, "expanded.identity", _ => "invalid id"));
    }

    [Fact]
    public void Versioned_template_document_round_trips_and_rejects_tampering()
    {
        var template = IdentityTemplate();
        var configuration = new GraphTemplateConfiguration(template, GraphTemplateJson.ComputeHash(template));

        var json = GraphTemplateJson.Serialize(configuration);
        var restored = GraphTemplateJson.DeserializeConfiguration(json);

        Assert.Equal(template.TemplateId, restored.Definition.TemplateId);
        Assert.Equal(template.Version, restored.Definition.Version);
        Assert.Equal(GraphTemplateJson.Serialize(template), GraphTemplateJson.Serialize(restored.Definition));
        Assert.Equal(configuration.ContentHash, restored.ContentHash);
        Assert.Equal(configuration.ContentHash, GraphTemplateJson.ComputeHash(restored.Definition));
        Assert.Throws<JsonException>(() => GraphTemplateJson.DeserializeConfiguration(
            json.Replace(configuration.ContentHash, new string('0', 64), StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => GraphTemplateJson.DeserializeConfiguration(
            json.Replace("\"formatVersion\":\"1.0\"", "\"formatVersion\":\"1.0\",\"formatVersion\":\"1.0\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => GraphTemplateJson.DeserializeConfiguration(
            json.Replace("{", "{\"unknown\":1,", StringComparison.Ordinal)));
    }

    [Fact]
    public void Template_import_has_bounded_bytes_and_null_shape_validation()
    {
        Assert.Throws<ArgumentException>(() => GraphTemplateJson.DeserializeConfiguration(
            new string('x', GraphTemplateLimits.MaximumJsonBytes + 1)));

        var invalid = IdentityTemplate() with { Body = IdentityTemplate().Body with { Nodes = null! } };

        var issues = GraphTemplateJson.Validate(invalid);

        Assert.Contains(issues, issue => issue.Code == "template.function.structure");
    }

    [Fact]
    public void Workflow_subgraph_extraction_requires_explicit_typed_boundaries()
    {
        var source = new WorkflowDefinition("1.27", "source", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("suffix", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") })
                .Add("concat", new(NodeTypes.TextConcat, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("suffix", new(new("suffix", "out"), new("concat", "right"), "data")));

        var template = GraphTemplateExtraction.Extract(source, ["suffix", "concat"],
            [new GraphTemplateInputBoundary("text", "Text", "string", "concat", "left")],
            [new GraphTemplateOutputBoundary("result", "Result", "string", "concat", "out")],
            "template.suffix", 1, "Suffix", "Adds a suffix.");

        Assert.Empty(GraphTemplateJson.Validate(template));
        Assert.Contains(template.Body.Connections.Values, edge => edge.Source.NodeId == "template-input.text" && edge.Target.NodeId == "concat");
        Assert.Contains(template.Body.Connections.Values, edge => edge.Source.NodeId == "concat" && edge.Target.NodeId == "template-output.result");
    }

    [Fact]
    public void Workflow_subgraph_extraction_rejects_hidden_incoming_data_edges()
    {
        var source = new WorkflowDefinition("1.27", "source", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("constant", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") })
                .Add("concat", new(NodeTypes.TextConcat, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("hidden", new(new("input", "out"), new("concat", "left"), "data")));

        var error = Assert.Throws<ArgumentException>(() => GraphTemplateExtraction.Extract(source, ["constant", "concat"],
            [], [new GraphTemplateOutputBoundary("result", "Result", "string", "concat", "out")],
            "template.invalid", 1, "Invalid", ""));

        Assert.Contains("must be declared as a template input boundary", error.Message);
    }

    private static GraphTemplateDefinition IdentityTemplate() => new(
        "template.identity", 1, "Identity", "Passes text through.",
        new GraphFunctionDefinition(
            "template.identity.body", 1, "Identity", "Passes text through.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("value", new("Value", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("result", new("Result", "string")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value" })
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new(new("input", "out"), new("output", "in"), "data"))));
}
