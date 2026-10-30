using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallJsonArrayTopologyTests
{
    [Fact]
    public void Analyzer_accepts_the_bounded_json_array_length_chain()
    {
        var definition = ArrayGraph();
        var validation = WorkflowValidator.Validate(definition);
        Assert.Empty(validation);
        var analysis = SequenceWorkflowCallJsonArrayTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join(" | ", analysis.Issues.Select(issue => issue.Message)));
        var path = Assert.Single(analysis.Paths);
        Assert.Equal("call", path.CallNodeId);
        Assert.Equal("to-array", path.ToArrayNodeId);
        Assert.Equal("length", path.LengthNodeId);
        Assert.Equal("integer", ArrayGraph().Nodes["output"].OutputDataType);
    }

    [Fact]
    public void Analyzer_rejects_a_second_array_node_or_unexpected_data_consumer()
    {
        var source = ArrayGraph();
        var invalid = source with
        {
            Nodes = source.Nodes.Add("second-length", new NodeDefinition(NodeTypes.ArrayLength, "1.0.0")),
            Connections = source.Connections.Add("second-length-output",
                new(new("second-length", "out"), new("output", "in"), "data"))
        };

        var analysis = SequenceWorkflowCallJsonArrayTopology.Analyze(invalid);

        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.call.json.array.length");
    }

    [Fact]
    public void Analyzer_preserves_schema_1_55_direct_call_graphs_without_inventing_array_nodes()
    {
        var source = ArrayGraph() with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph,
            Nodes = ArrayGraph().Nodes.Remove("to-array").Remove("length")
                .SetItem("output", ArrayGraph().Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json }),
            Connections = ArrayGraph().Connections
                .Remove("call-array")
                .Remove("array-length")
                .Remove("length-output")
                .Add("call-output-data", new(new("call", "out"), new("output", "in"), "data"))
        };

        var promoted = GraphEditing.EnableSequenceWorkflowCallJsonArrayGraph(source);

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph, promoted.SchemaVersion);
        Assert.Empty(SequenceWorkflowCallJsonArrayTopology.Analyze(promoted).Issues);
        Assert.DoesNotContain(promoted.Nodes.Values, node => node.Type is NodeTypes.JsonToArray or NodeTypes.ArrayLength);
    }

    [Fact]
    public void Authoring_adds_the_bounded_length_predicate_and_failure_route()
    {
        var source = ArrayGraph();

        var updated = WorkflowCallEditing.AppendSequenceWorkflowCallJsonControlDraft(source, "call", 3);

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph, updated.SchemaVersion);
        var analysis = SequenceWorkflowCallJsonControlTopology.Analyze(updated);
        Assert.True(analysis.IsValid, string.Join(" | ", analysis.Issues.Select(issue => issue.Message)));
        Assert.Empty(WorkflowValidator.Validate(updated));
        Assert.Contains(updated.Nodes.Values, node => node.Type == NodeTypes.BooleanBranch);
        Assert.Contains(updated.Nodes.Values, node => node.Type == NodeTypes.Terminate && node.Termination?.Outcome == WorkflowTerminationOutcomes.Failure);
    }

    private static WorkflowDefinition ArrayGraph()
    {
        var child = WorkflowRevision.Create(JsonChild("array-child"), "array-test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("sibling", "Sibling")]) },
            ["call"] = call,
            ["sibling"] = new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" },
            ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
            ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
            ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
            ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
            ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["call-array"] = new(new("call", "out"), new("to-array", "in"), "data"),
            ["array-length"] = new(new("to-array", "out"), new("length", "in"), "data"),
            ["length-output"] = new(new("length", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph, "array-parent", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition JsonChild(string id) => new("1.13", id, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));
}
