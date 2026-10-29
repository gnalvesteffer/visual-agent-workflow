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
