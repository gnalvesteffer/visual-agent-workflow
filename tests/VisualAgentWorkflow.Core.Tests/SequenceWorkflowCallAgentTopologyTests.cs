using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallAgentTopologyTests
{
    [Fact]
    public void Schema_1_52_accepts_a_terminal_agent_after_a_pinned_string_call()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var definition = CallAgentGraph(child);

        var analysis = SequenceWorkflowCallAgentTopology.Analyze(definition);

        Assert.True(analysis.IsValid, string.Join("; ", analysis.Issues.Select(issue => $"{issue.Code}:{issue.Message}")));
        var path = Assert.Single(analysis.Paths);
        Assert.Equal("call", path.CallNodeId);
        Assert.Equal("agent", path.AgentNodeId);
        Assert.Equal(child.RevisionId, path.ChildRevisionId);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_1_52_preserves_a_terminal_call_and_rejects_agent_before_call()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var valid = CallAgentGraph(child) with
        {
            Nodes = CallAgentGraph(child).Nodes.Remove("agent"),
            ExecutionConnections = CallAgentGraph(child).ExecutionConnections!
                .Remove("call-agent")
                .Add("call-output", new(new("call", "out"), new("output", "execute"), "execution"))
                .Remove("agent-output")
        };
        Assert.Empty(WorkflowValidator.Validate(valid));

        var invalid = CallAgentGraph(child) with
        {
            ExecutionConnections = CallAgentGraph(child).ExecutionConnections!
                .Remove("sequence-call")
                .Remove("call-agent")
                .Remove("agent-output")
                .Add("sequence-agent", new(new("sequence", "then.call"), new("agent", "execute"), "execution"))
                .Add("agent-call", new(new("agent", "out"), new("call", "execute"), "execution"))
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "sequence.call.agent.order");
    }

    [Fact]
    public void Schema_1_52_rejects_a_second_successor_after_the_agent()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var definition = CallAgentGraph(child) with
        {
            Nodes = CallAgentGraph(child).Nodes.Add("extra", new(NodeTypes.McpTool, "1.0.0", new("extra", new string('a', 64))) { McpArgumentsJson = "{}" }),
            ExecutionConnections = CallAgentGraph(child).ExecutionConnections!
                .Remove("agent-output")
                .Add("agent-extra", new(new("agent", "out"), new("extra", "execute"), "execution"))
                .Add("extra-output", new(new("extra", "then"), new("output", "execute"), "execution"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code is "sequence.call.agent.terminal" or "sequence.call.agent.order");
    }

    [Fact]
    public void Schema_1_52_package_declares_the_agent_provider_contract()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(
            CallAgentGraph(child), WorkflowLayout.Create(CallAgentGraph(child))));

        Assert.Contains(package.Requirements, requirement => requirement.Kind == "agent-provider-contract" &&
            requirement.Id == "provider" && requirement.ContractHash == new string('b', 64));
    }

    [Fact]
    public void Schema_1_53_preserves_a_valid_schema_1_52_agent_path()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var promoted = GraphEditing.EnableSequenceWorkflowCallJsonGraph(CallAgentGraph(child));

        Assert.Empty(WorkflowValidator.Validate(promoted));
        Assert.True(SequenceWorkflowCallAgentTopology.Analyze(promoted).IsValid);
    }

    [Fact]
    public void Explicit_migration_promotes_a_valid_schema_1_51_call_transform_graph()
    {
        var child = WorkflowRevision.Create(ChildDefinition("child"), "test");
        var source = CallTransformGraph(child);
        var revision = WorkflowRevision.Create(source, "test");

        var attempt = WorkflowMigrationRegistry.TryMigrate(new(revision, ["sequence-workflow-call-agents"], "test"));

        Assert.True(attempt.Succeeded, string.Join("; ", attempt.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph, attempt.Result!.TargetRevision.Definition.SchemaVersion);
        Assert.Equal(revision.RevisionId, attempt.Result.TargetRevision.ParentRevisionId);
        Assert.Equal(source.Nodes.Keys, attempt.Result.TargetRevision.Definition.Nodes.Keys);
        Assert.Equal(source.ExecutionConnections!.Keys, attempt.Result.TargetRevision.Definition.ExecutionConnections!.Keys);
    }

    private static WorkflowDefinition CallAgentGraph(WorkflowRevision child)
    {
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("provider", null, "Return the supplied text.", [])
            {
                ProviderContractHash = new string('b', 64),
                MaxTurns = 1,
                MaxTotalTokens = 4000
            }
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("sibling", "Sibling")]) })
            .Add("call", call)
            .Add("agent", agent)
            .Add("sibling", new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-call", new(new("sequence", "then.call"), new("call", "execute"), "execution"))
            .Add("call-agent", new(new("call", "out"), new("agent", "execute"), "execution"))
            .Add("agent-output", new(new("agent", "out"), new("output", "execute"), "execution"))
            .Add("sequence-sibling", new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"))
            .Add("sibling-output", new(new("sibling", "then"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph, "parent", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition CallTransformGraph(WorkflowRevision child)
    {
        var source = CallAgentGraph(child);
        var execution = source.ExecutionConnections!
            .Remove("call-agent")
            .Remove("agent-output")
            .Add("call-transform", new(new("call", "out"), new("transform", "execute"), "execution"))
            .Add("transform-output", new(new("transform", "then"), new("output", "execute"), "execution"));
        return source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph,
            Nodes = source.Nodes.Remove("agent").Add("transform", new(NodeTypes.SequenceUppercase, "1.0.0")),
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition ChildDefinition(string workflowId) => new("1.0", workflowId, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("value", new(new("input", "out"), new("output", "in"), "data")));
}
