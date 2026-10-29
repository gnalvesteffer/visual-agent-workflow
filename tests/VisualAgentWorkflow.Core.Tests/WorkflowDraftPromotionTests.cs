using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowDraftPromotionTests
{
    [Fact]
    public void Promotes_only_the_canonical_invalid_1_49_draft_into_a_valid_1_50_revision()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child);
        var candidate = WorkflowDraftPromotionCandidate.Create(source, WorkflowLayout.Create(source), 7);

        var result = WorkflowDraftPromotion.TryPromote(candidate, "studio", Guid.NewGuid());

        Assert.Equal(WorkflowSchemaVersions.SequenceTransformChainGraph, result.SourceCandidate.Definition.SchemaVersion);
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, result.TargetRevision.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(result.TargetRevision.Definition));
        Assert.NotEmpty(result.Provenance.SourceDiagnostics);
        Assert.Equal(candidate.DefinitionHash, result.Provenance.SourceDefinitionHash);
        Assert.Equal(candidate.LayoutHash, result.Provenance.SourceLayoutHash);
        Assert.Equal(result.TargetRevision.RevisionId, result.Provenance.TargetRevisionId);
        Assert.Equal(result.Provenance, result.TargetRevision.DraftPromotionProvenance);
    }

    [Fact]
    public void Rejects_a_candidate_when_source_diagnostics_or_hashes_are_tampered()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child);
        var candidate = WorkflowDraftPromotionCandidate.Create(source, WorkflowLayout.Create(source), 7);

        Assert.Throws<ArgumentException>(() => WorkflowDraftPromotion.TryPromote(
            candidate with { DefinitionHash = new string('0', 64) }, "studio"));
        Assert.Throws<ArgumentException>(() => WorkflowDraftPromotion.TryPromote(
            candidate with { Diagnostics = [new("tampered", "draft", "not source evidence")] }, "studio"));
    }

    [Fact]
    public void Promotion_package_round_trip_preserves_the_invalid_source_envelope()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child);
        var candidate = WorkflowDraftPromotionCandidate.Create(source, WorkflowLayout.Create(source), 7);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "studio", Guid.NewGuid());

        var restored = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(promotion));

        Assert.Equal("1.2", restored.FormatVersion);
        Assert.NotNull(restored.DraftPromotionProvenance);
        Assert.Equal(candidate.DefinitionHash, restored.DraftPromotionProvenance!.Draft.SourceDefinitionHash);
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, restored.Definition.SchemaVersion);
    }

    [Fact]
    public void Promoted_public_call_package_round_trip_preserves_child_identity_layout_and_hashes()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child);
        var candidate = WorkflowDraftPromotionCandidate.Create(source, WorkflowLayout.Create(source), 7);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "studio", Guid.NewGuid());

        var json = WorkflowPackageJson.Serialize(promotion);
        var restored = WorkflowPackageJson.Deserialize(json);
        var restoredCall = restored.Definition.Nodes["call"].WorkflowCall!;
        var originalCall = promotion.TargetRevision.Definition.Nodes["call"].WorkflowCall!;

        Assert.Equal("1.2", restored.FormatVersion);
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, restored.Definition.SchemaVersion);
        Assert.Equal(originalCall.Identity, restoredCall.Identity);
        Assert.Equal(WorkflowJson.Serialize(originalCall.Definition), WorkflowJson.Serialize(restoredCall.Definition));
        Assert.Equal(promotion.TargetRevision.ContentHash, restored.DefinitionHash);
        Assert.Equal(WorkflowLayoutJson.Serialize(promotion.TargetRevision.Layout!, promotion.TargetRevision.Definition),
            WorkflowLayoutJson.Serialize(restored.Layout, restored.Definition));
        Assert.NotNull(restored.DraftPromotionProvenance);
        Assert.Equal(restored.DefinitionHash, WorkflowJson.ComputeHash(restored.Definition));
        Assert.Equal(restored.LayoutHash, WorkflowLayoutJson.ComputeHash(restored.Layout, restored.Definition));
    }

    [Fact]
    public void Unsupported_post_call_topology_remains_a_saveable_draft_but_cannot_be_published()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var source = CallGraph(child) with
        {
            Nodes = CallGraph(child).Nodes.Add("transform", new(NodeTypes.SequenceUppercase, "1.0.0")),
            ExecutionConnections = CallGraph(child).ExecutionConnections!
                .SetItem("call-output", new(new("call", "out"), new("transform", "execute"), "execution"))
                .Add("transform-output", new(new("transform", "then"), new("output", "execute"), "execution"))
        };

        var issues = WorkflowValidator.Validate(source);

        Assert.Contains(issues, issue => issue.Code == "execution.node.unsupported");
        Assert.Contains(issues, issue => issue.Code == "sequence.call.path.terminal");
        var draftJson = WorkflowJson.SerializeDraft(source);
        Assert.Equal(draftJson, WorkflowJson.SerializeDraft(WorkflowJson.DeserializeDraft(draftJson)));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(source));
    }

    private static WorkflowDefinition CallGraph(WorkflowRevision child) =>
        new(WorkflowSchemaVersions.SequenceTransformChainGraph, "draft-call", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "Call"), new("sibling", "Sibling")])
                })
                .Add("call", new(NodeTypes.WorkflowCall, "1.0.0")
                {
                    WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
                })
                .Add("sibling", new(NodeTypes.McpTool, "1.0.0", new("test.tool", new string('0', 64)))
                {
                    McpArgumentsJson = "{}"
                })
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
                .Add("sequence-call", new(new("sequence", "then.call"), new("call", "execute"), "execution"))
                .Add("call-output", new(new("call", "out"), new("output", "execute"), "execution"))
                .Add("sequence-sibling", new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"))
                .Add("sibling-output", new(new("sibling", "then"), new("output", "execute"), "execution"))
        };

    private static WorkflowDefinition Baseline(string workflowId) => new("1.0", workflowId, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("edge", new(new("input", "out"), new("output", "in"), "data")));
}
