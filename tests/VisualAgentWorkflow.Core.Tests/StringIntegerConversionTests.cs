using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class StringIntegerConversionTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("+00042", 42)]
    [InlineData("-00042", -42)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void Parser_accepts_the_portable_signed_decimal_grammar(string input, long expected)
    {
        var result = StringIntegerConversion.Parse(input);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Value);
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("+", "syntax")]
    [InlineData(" 42", "syntax")]
    [InlineData("42 ", "syntax")]
    [InlineData("1_000", "syntax")]
    [InlineData("1.0", "syntax")]
    [InlineData("1e3", "syntax")]
    [InlineData("٤٢", "syntax")]
    [InlineData("9223372036854775808", "overflow")]
    [InlineData("-9223372036854775809", "overflow")]
    [InlineData("12345678901234567890X", "too-long")]
    public void Parser_rejects_nonportable_values_with_stable_bounded_evidence(string input, string reason)
    {
        var result = StringIntegerConversion.Parse(input);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal("string-to-integer.invalid", result.Failure!.Code);
        Assert.Equal("1", result.Failure.ParserVersion);
        Assert.Equal(reason, result.Failure.Reason);
        Assert.Equal(input.Length, result.Failure.InputLength);
    }

    [Fact]
    public void Conversion_node_has_typed_ports_and_is_rejected_by_older_schemas()
    {
        var node = new NodeDefinition(NodeTypes.StringToInteger, "1.0.0");

        Assert.Equal(("in", "string"), Assert.Single(WorkflowPorts.ExplicitDataInputs(node)) is { } input ? (input.Id, input.DataType) : default);
        Assert.Equal(("out", "integer"), Assert.Single(WorkflowPorts.ExplicitDataOutputs(node)) is { } output ? (output.Id, output.DataType) : default);
        Assert.Contains(WorkflowNodeContractCatalog.All, contract => contract.Type == NodeTypes.StringToInteger && contract.IsPure);

        var legacy = ConversionWorkflow(WorkflowSchemaVersions.IntegerGraph);
        Assert.Contains(WorkflowValidator.Validate(legacy), issue => issue.Code == "node.schema");
    }

    [Fact]
    public void Conversion_graph_is_valid_only_in_schema_139_and_preserves_old_definition_hashes()
    {
        var legacy = new WorkflowDefinition(WorkflowSchemaVersions.ExplicitGraph, "legacy", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("result", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("activate", new(new("input", "then"), new("output", "execute"), "execution"))
        };
        var legacyHash = WorkflowJson.ComputeHash(legacy);
        var upgraded = ConversionWorkflow(WorkflowSchemaVersions.StringIntegerConversionGraph);

        Assert.Empty(WorkflowValidator.Validate(upgraded));
        Assert.Equal(legacyHash, WorkflowJson.ComputeHash(legacy));
    }

    [Fact]
    public void Graph_functions_reject_conversion_until_their_contract_is_versioned()
    {
        var function = new GraphFunctionDefinition("convert", 1, "Convert", "", 
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.Add("input", new("Input", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.Add("output", new("Output", "integer")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "input", OutputDataType = "string" })
                .Add("convert", new(NodeTypes.StringToInteger, "1.0.0"))
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "output", InputDataType = "integer" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input", new(new("input", "out"), new("convert", "in"), "data"))
                .Add("output", new(new("convert", "out"), new("output", "in"), "data")));

        Assert.Contains(GraphFunctionJson.Validate(function), issue => issue.Code == "function.node.unsupported");
    }

    [Fact]
    public void Explicit_insertion_connects_an_empty_target_without_implicit_coercion()
    {
        var definition = IntegerInputGraph();

        var inserted = GraphEditing.InsertStringIntegerConversion(definition, "input", "out", "add", "left");

        Assert.Equal(WorkflowSchemaVersions.StringIntegerConversionGraph, inserted.SchemaVersion);
        var conversion = Assert.Single(inserted.Nodes, pair => pair.Value.Type == NodeTypes.StringToInteger).Key;
        Assert.Contains(inserted.Connections.Values, edge =>
            edge.Source == new PortReference("input", "out") &&
            edge.Target == new PortReference(conversion, "in"));
        Assert.Contains(inserted.Connections.Values, edge =>
            edge.Source == new PortReference(conversion, "out") &&
            edge.Target == new PortReference("add", "left"));
        Assert.Contains(inserted.ExecutionConnections!, edge =>
            edge.Value.Source == new PortReference("input", "then") &&
            edge.Value.Target == new PortReference("output", "execute"));
        Assert.Empty(WorkflowValidator.Validate(inserted));
    }

    [Fact]
    public void Explicit_insertion_rejects_occupied_pins_and_unsupported_schema_without_mutation()
    {
        var definition = IntegerInputGraph() with
        {
            Nodes = IntegerInputGraph().Nodes.Add("left", new(NodeTypes.IntegerConstant, "1.0.0")
            {
                IntegerConstant = new(1)
            }),
            Connections = IntegerInputGraph().Connections.Add("occupied",
                new(new("left", "out"), new("add", "left"), "data"))
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            GraphEditing.InsertStringIntegerConversion(definition, "input", "out", "add", "left"));

        Assert.Contains("target input", exception.Message, StringComparison.Ordinal);
        Assert.Equal(WorkflowSchemaVersions.StringIntegerConversionGraph, definition.SchemaVersion);
        Assert.Throws<ArgumentException>(() =>
            GraphEditing.InsertStringIntegerConversion(definition with { SchemaVersion = WorkflowSchemaVersions.IntegerGraph },
                "input", "out", "add", "left"));
    }

    private static WorkflowDefinition ConversionWorkflow(string schema) => new(schema, "conversion", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("convert", new(NodeTypes.StringToInteger, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "integer" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("value", new(new("input", "out"), new("convert", "in"), "data"))
            .Add("result", new(new("convert", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate", new(new("input", "then"), new("output", "execute"), "execution"))
    };

    private static WorkflowDefinition IntegerInputGraph() => new(WorkflowSchemaVersions.StringIntegerConversionGraph, "integer-input", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("right", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(2) })
            .Add("add", new(NodeTypes.IntegerAdd, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "integer" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("right", new(new("right", "out"), new("add", "right"), "data"))
            .Add("result", new(new("add", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate", new(new("input", "then"), new("output", "execute"), "execution"))
    };
}
