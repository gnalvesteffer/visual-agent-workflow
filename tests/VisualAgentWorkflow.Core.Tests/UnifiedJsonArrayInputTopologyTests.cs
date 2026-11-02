using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class UnifiedJsonArrayInputTopologyTests
{
    [Fact]
    public void Unified_schema_accepts_direct_json_array_count_without_a_workflow_call()
    {
        var definition = ArrayInputGraph();

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.True(UnifiedJsonArrayInputTopology.Analyze(definition).IsValid);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("json")]
    public void Unified_schema_rejects_an_output_type_other_than_integer(string outputType)
    {
        var definition = ArrayInputGraph() with
        {
            Nodes = ArrayInputGraph().Nodes.SetItem("output",
                ArrayInputGraph().Nodes["output"] with { OutputDataType = outputType })
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "unified.json-array.output");
    }

    [Fact]
    public void Unified_schema_rejects_extra_nodes_and_data_consumers_in_this_slice()
    {
        var source = ArrayInputGraph();
        var definition = source with
        {
            Nodes = source.Nodes.Add("unused", new(NodeTypes.StringConstant, "1.0.0")
                { StringConstant = new("unused") }),
            Connections = source.Connections.Add("extra-consumer",
                new(new("input", "out"), new("unused", "in"), "data"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue =>
            issue.Code is "unified.json-array.nodes" or "unified.json-array.data-shape");
    }

    [Fact]
    public void Array_nodes_do_not_admit_the_unified_topology_as_a_historical_schema()
    {
        var definition = ArrayInputGraph();
        var historical = definition with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph };

        Assert.Contains(UnifiedJsonArrayInputTopology.Analyze(historical).Issues,
            issue => issue.Code == "unified.json-array.schema");
        Assert.DoesNotContain(WorkflowValidator.Validate(definition), issue => issue.Code == "schema.unsupported");
    }

    private static WorkflowDefinition ArrayInputGraph()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
            ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-array"] = new(new("input", "out"), new("to-array", "in"), "data"),
            ["array-length"] = new(new("to-array", "out"), new("length", "in"), "data"),
            ["length-output"] = new(new("length", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-json-array-input", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }
}
