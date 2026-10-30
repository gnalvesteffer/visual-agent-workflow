using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallJsonMergeTopologyTests
{
    [Fact]
    public void Analyzer_accepts_the_bounded_project_and_merge_fanout()
    {
        var definition = MergeGraph();

        var analysis = SequenceWorkflowCallJsonMergeTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join(" | ", analysis.Issues.Select(issue => issue.Message)));
        var path = Assert.Single(analysis.Paths);
        Assert.Equal("call", path.CallNodeId);
        Assert.Equal("project", path.ProjectionNodeId);
        Assert.Equal("merge", path.MergeNodeId);
    }

    [Fact]
    public void Analyzer_rejects_extra_consumer_and_merge_chain()
    {
        var source = MergeGraph();
        var invalid = source with
        {
            Nodes = source.Nodes
                .Add("second", new NodeDefinition(NodeTypes.JsonMerge, "1.0.0"))
                .SetItem("output", source.Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json }),
            Connections = source.Connections
                .Remove("merge-output")
                .Add("merge-second-left", new(new("merge", "out"), new("second", "left"), "data"))
                .Add("call-second", new(new("call", "out"), new("second", "right"), "data"))
                .Add("second-output", new(new("second", "out"), new("output", "in"), "data"))
        };

        var analysis = SequenceWorkflowCallJsonMergeTopology.Analyze(invalid);

        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.call.json.merge.limit");
    }

    [Fact]
    public void Promotion_preserves_the_merge_graph_identity_and_layout_independent_definition()
    {
        var merge = MergeGraph();
        var source = merge with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph,
            Nodes = merge.Nodes.Remove("project").Remove("merge"),
            Connections = merge.Connections.Remove("call-merge").Remove("call-project").Remove("project-merge").Remove("merge-output")
                .Add("call-json-output", new(new("call", "out"), new("output", "in"), "data"))
        };
        Assert.Empty(WorkflowValidator.Validate(source));

        var promoted = GraphEditing.EnableSequenceWorkflowCallJsonMergeGraph(source);

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph, promoted.SchemaVersion);
        Assert.Equal(source.Nodes, promoted.Nodes);
        Assert.Equal(source.Connections, promoted.Connections);
        Assert.Equal(source.Nodes["call"].WorkflowCall!.ContentHash, promoted.Nodes["call"].WorkflowCall!.ContentHash);
    }

    private static WorkflowDefinition MergeGraph()
    {
        var child = WorkflowRevision.Create(JsonChild("merge-child"), "test");
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
            ["project"] = new(NodeTypes.JsonProject, "1.0.0")
            {
                JsonProjection = new([new JsonProjectionField("answer", "/answer")])
            },
            ["merge"] = new(NodeTypes.JsonMerge, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
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
            ["call-merge"] = new(new("call", "out"), new("merge", "left"), "data"),
            ["call-project"] = new(new("call", "out"), new("project", "in"), "data"),
            ["project-merge"] = new(new("project", "out"), new("merge", "right"), "data"),
            ["merge-output"] = new(new("merge", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        return new(WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph, "merge-parent", "input", nodes, data)
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
