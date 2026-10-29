using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallTopologyTests
{
    [Fact]
    public void Finds_one_pinned_string_call_path_without_admitting_schema_1_49()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var definition = CallGraph(child);

        var analysis = SequenceWorkflowCallTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        var path = Assert.Single(analysis.Paths);
        Assert.Equal("call", path.CallNodeId);
        Assert.Equal(child.RevisionId, path.ChildRevisionId);
        Assert.Contains("workflowCall.configuration", string.Join(";", WorkflowValidator.Validate(definition).Select(issue => issue.Code)));
    }

    [Fact]
    public void Schema_1_50_admits_a_terminal_call_with_an_ordinary_mcp_sibling_path()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var definition = CallGraph(child, WorkflowSchemaVersions.SequenceWorkflowCallGraph);

        var issues = WorkflowValidator.Validate(definition);

        Assert.Empty(issues);
        Assert.True(SequenceWorkflowCallTopology.Analyze(definition).IsValid);
    }

    [Fact]
    public void Rejects_a_transform_after_the_call_and_a_tampered_child_hash()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, new string('0', 64), child.Definition)
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("other", "Other")]) })
            .Add("call", call)
            .Add("transform", new(NodeTypes.SequenceUppercase, "1.0.0"))
            .Add("other", new(NodeTypes.Uppercase, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-call", new(new("sequence", "then.call"), new("call", "execute"), "execution"))
            .Add("call-transform", new(new("call", "out"), new("transform", "execute"), "execution"))
            .Add("transform-output", new(new("transform", "then"), new("output", "execute"), "execution"))
            .Add("sequence-other", new(new("sequence", "then.other"), new("other", "execute"), "execution"))
            .Add("other-output", new(new("other", "out"), new("output", "execute"), "execution"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceTransformChainGraph, "call-topology", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = execution
        };

        var analysis = SequenceWorkflowCallTopology.Analyze(definition);

        Assert.False(analysis.IsValid);
        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.call.binding");
        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.call.path.terminal");
    }

    [Fact]
    public void Schema_1_51_allows_exactly_one_transform_after_the_call()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child, WorkflowSchemaVersions.SequenceWorkflowCallGraph);
        var execution = source.ExecutionConnections!
            .SetItem("call-output", source.ExecutionConnections["call-output"] with { Target = new("transform", "execute") })
            .Add("transform-output", new(new("transform", "then"), new("output", "execute"), "execution"));
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph,
            Nodes = source.Nodes.Add("transform", new(NodeTypes.SequenceUppercase, "1.0.0")),
            ExecutionConnections = execution
        };

        var analysis = SequenceWorkflowCallTransformTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        Assert.Equal("transform", Assert.Single(analysis.Paths).TransformNodeId);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    private static WorkflowDefinition CallGraph(WorkflowRevision child, string schema = WorkflowSchemaVersions.SequenceTransformChainGraph)
    {
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("sibling", "Sibling")]) })
            .Add("call", call)
            .Add("sibling", new(NodeTypes.McpTool, "1.0.0", new("test.tool", new string('0', 64))) { McpArgumentsJson = "{}" })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-call", new(new("sequence", "then.call"), new("call", "execute"), "execution"))
            .Add("call-output", new(new("call", "out"), new("output", "execute"), "execution"))
            .Add("sequence-sibling", new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"))
            .Add("sibling-output", new(new("sibling", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(schema, "call-topology", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition Baseline(string workflowId) => new("1.0", workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());
}
