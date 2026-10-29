using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceWorkflowCallEditingTests
{
    [Fact]
    public void Appends_a_pinned_call_to_an_empty_or_direct_mcp_sequence_path_as_a_saveable_draft()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = PathGraph();

        var emptyPath = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(source, "sequence", "empty", new(1, child));
        var emptyCall = Assert.Single(emptyPath.Nodes, pair => pair.Value.Type == NodeTypes.WorkflowCall);
        Assert.Equal(WorkflowSchemaVersions.SequenceTransformChainGraph, emptyPath.SchemaVersion);
        Assert.Equal(new PortReference("sequence", "then.empty"),
            emptyPath.ExecutionConnections!["sequence-empty"].Source);
        Assert.Equal(new PortReference(emptyCall.Key, "execute"),
            emptyPath.ExecutionConnections!["sequence-empty"].Target);
        Assert.Contains(emptyPath.ExecutionConnections.Values, edge =>
            edge.Source == new PortReference(emptyCall.Key, "out") && edge.Target == new PortReference("output", "execute"));
        Assert.NotEmpty(WorkflowValidator.Validate(emptyPath));
        _ = WorkflowJson.SerializeDraft(emptyPath);

        var mcpPath = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(source, "sequence", "mcp", new(1, child));
        var mcpCall = Assert.Single(mcpPath.Nodes, pair => pair.Value.Type == NodeTypes.WorkflowCall);
        Assert.Equal(new PortReference("mcp", "then"), mcpPath.ExecutionConnections!["mcp-output"].Source);
        Assert.Equal(new PortReference(mcpCall.Key, "execute"), mcpPath.ExecutionConnections!["mcp-output"].Target);
        Assert.NotEmpty(WorkflowValidator.Validate(mcpPath));
        _ = WorkflowJson.SerializeDraft(mcpPath);
    }

    [Fact]
    public void Promoted_result_is_valid_schema_1_50_and_rejects_ineligible_paths()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = PathGraph();
        var draft = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(source, "sequence", "mcp", new(1, child));
        var candidate = WorkflowDraftPromotionCandidate.Create(draft, WorkflowLayout.Create(draft), 3);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "test");

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, promotion.TargetRevision.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(promotion.TargetRevision.Definition));
        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.AppendSequenceWorkflowCallDraft(
            source with { Nodes = source.Nodes.SetItem("mcp", new(NodeTypes.SequenceUppercase, "1.0.0")) },
            "sequence", "mcp", new(1, child)));
        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.AppendSequenceWorkflowCallDraft(
            source, "sequence", "mcp", new(1, WorkflowRevision.Create(Baseline("parent"), "test"))));
    }

    [Fact]
    public void Draft_upgrade_preserves_call_node_identity()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var newer = WorkflowRevision.Create(Baseline("child"), "test");
        var parent = PathGraph();
        var draft = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(parent, "sequence", "empty", new(1, child));
        var callId = draft.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key;

        var result = WorkflowCallEditing.UpgradeSequenceWorkflowCallDraft(draft, callId, new(2, newer));

        Assert.Equal(callId, result.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key);
        Assert.Empty(result.DroppedParameterMappings);
    }

    private static WorkflowDefinition PathGraph()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            { Sequence = new([new("mcp", "Tool path"), new("empty", "Empty path")]) })
            .Add("mcp", new(NodeTypes.McpTool, "1.0.0", new("demo.tool", new string('a', 64))) { McpArgumentsJson = "{}" })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-mcp", new(new("sequence", "then.mcp"), new("mcp", "execute"), "execution"))
            .Add("mcp-output", new(new("mcp", "then"), new("output", "execute"), "execution"))
            .Add("sequence-empty", new(new("sequence", "then.empty"), new("output", "execute"), "execution"));
        return new(WorkflowSchemaVersions.SequenceTransformChainGraph, "parent", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition Baseline(string workflowId) => new("1.0", workflowId, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("edge", new(new("input", "out"), new("output", "in"), "data")));
}
