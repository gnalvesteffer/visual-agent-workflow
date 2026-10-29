using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallJsonTopologyTests
{
    [Fact]
    public void Schema_1_53_accepts_a_pinned_json_call_with_a_direct_output()
    {
        var child = WorkflowRevision.Create(JsonChild("child"), "test");
        var definition = Parent(child);

        var analysis = SequenceWorkflowCallJsonTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        Assert.Equal("call", Assert.Single(analysis.Paths).CallNodeId);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_1_53_rejects_a_string_child_contract()
    {
        var child = WorkflowRevision.Create(StringChild("child"), "test");
        var definition = Parent(child);

        var analysis = SequenceWorkflowCallJsonTopology.Analyze(definition);

        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.call.json.contract");
    }

    [Fact]
    public void Schema_1_53_accepts_one_data_only_json_selector_before_output()
    {
        var child = WorkflowRevision.Create(JsonChild("child"), "test");
        var direct = Parent(child);
        var definition = direct with
        {
            Nodes = direct.Nodes
                .SetItem("select", new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0")
                {
                    JsonPropertySelection = new("/answer")
                })
                .SetItem("output", direct.Nodes["output"] with { OutputDataType = WorkflowDataTypes.String }),
            Connections = direct.Connections
                .Remove("call-json-output")
                .Add("call-select", new(new("call", "out"), new("select", "in"), "data"))
                .Add("select-output", new(new("select", "out"), new("output", "in"), "data"))
        };

        var analysis = SequenceWorkflowCallJsonTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        Assert.Equal("select", Assert.Single(analysis.Paths).JsonNodeId);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_1_54_accepts_project_then_selector_as_a_typed_data_chain()
    {
        var child = WorkflowRevision.Create(JsonChild("child"), "test");
        var definition = Chain(Parent(child), NodeTypes.JsonSelectString, outputType: WorkflowDataTypes.String);

        var analysis = SequenceWorkflowCallJsonChainTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        var path = Assert.Single(analysis.Paths);
        Assert.Equal(2, path.JsonNodeIds.Length);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_1_54_rejects_selector_to_project_and_three_node_chains()
    {
        var child = WorkflowRevision.Create(JsonChild("child"), "test");
        var selectorFirst = Chain(Parent(child), NodeTypes.JsonProject, outputType: WorkflowDataTypes.Json, firstType: NodeTypes.JsonSelectString);
        var twoProjects = Chain(Parent(child), NodeTypes.JsonProject, outputType: WorkflowDataTypes.Json);
        var tooLong = twoProjects with
        {
            Nodes = twoProjects.Nodes.Add("third", new NodeDefinition(NodeTypes.JsonProject, "1.0.0")
            {
                JsonProjection = new([new JsonProjectionField("answer", "/answer")])
            }),
            Connections = twoProjects.Connections.Remove("second-output")
                .Add("second-third", new(new("second", "out"), new("third", "in"), "data"))
                .Add("third-output", new(new("third", "out"), new("output", "in"), "data"))
        };

        Assert.Contains(SequenceWorkflowCallJsonChainTopology.Analyze(selectorFirst).Issues,
            issue => issue.Code == "sequence.call.json.chain.type");
        Assert.Contains(SequenceWorkflowCallJsonChainTopology.Analyze(tooLong).Issues,
            issue => issue.Code == "sequence.call.json.chain.limit");
    }

    private static WorkflowDefinition Chain(WorkflowDefinition source, string secondType, string outputType,
        string firstType = NodeTypes.JsonProject)
    {
        var first = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph,
            Nodes = source.Nodes
                .Add("project", new NodeDefinition(firstType, "1.0.0")
                {
                    JsonProjection = firstType == NodeTypes.JsonProject ? new([new JsonProjectionField("answer", "/answer")]) : null,
                    JsonPropertySelection = firstType == NodeTypes.JsonSelectString ? new("/answer") : null
                })
                .SetItem("output", source.Nodes["output"] with { OutputDataType = firstType == NodeTypes.JsonSelectString ? WorkflowDataTypes.String : outputType })
        };
        var firstEdge = first.Connections.Remove("call-json-output")
            .Add("call-project", new(new("call", "out"), new("project", "in"), "data"));
        var second = first;
        if (secondType is not null)
        {
            second = second with
            {
                Nodes = second.Nodes.Add("second", new NodeDefinition(secondType, "1.0.0")
                {
                    JsonProjection = secondType == NodeTypes.JsonProject ? new([new JsonProjectionField("answer", "/answer")]) : null,
                    JsonPropertySelection = secondType == NodeTypes.JsonSelectString ? new("/answer") : null
                }).SetItem("output", second.Nodes["output"] with { OutputDataType = outputType }),
                Connections = firstEdge.Remove("call-json-output")
                    .Add("project-second", new(new("project", "out"), new("second", "in"), "data"))
                    .Add("second-output", new(new("second", "out"), new("output", "in"), "data"))
            };
        }
        return second;
    }

    private static WorkflowDefinition Parent(WorkflowRevision child)
    {
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
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
            ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
            ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
            ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["call-json-output"] = new(new("call", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph, "parent", "input",
            nodes.ToImmutableSortedDictionary(StringComparer.Ordinal), data.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary(StringComparer.Ordinal)
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

    private static WorkflowDefinition StringChild(string id) => new("1.0", id, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));
}
