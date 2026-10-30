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

    [Fact]
    public void Appends_one_transform_after_a_published_call_and_promotes_to_schema_1_51()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var callDraft = WorkflowCallEditing.AppendSequenceWorkflowCallDraft(PathGraph(), "sequence", "empty", new(1, child));
        var callRevision = WorkflowDraftPromotion.TryPromote(
            WorkflowDraftPromotionCandidate.Create(callDraft, WorkflowLayout.Create(callDraft), 1), "test").TargetRevision;
        var transformDraft = WorkflowCallEditing.AppendSequenceWorkflowCallTransformDraft(
            callRevision.Definition, callRevision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key,
            NodeTypes.SequenceReverse);

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, transformDraft.SchemaVersion);
        Assert.Contains(WorkflowValidator.Validate(transformDraft), issue => issue.Code == "execution.node.unsupported");
        var candidate = WorkflowDraftPromotionCandidate.Create(transformDraft, WorkflowLayout.Create(transformDraft), 2);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "test");

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph, promotion.TargetRevision.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(promotion.TargetRevision.Definition));
    }

    [Fact]
    public void Appends_a_new_pinned_json_path_without_reinterpreting_existing_nodes()
    {
        var child = WorkflowRevision.Create(JsonBaseline("json-child"), "test");
        var source = PathGraph() with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph };

        var draft = WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(source, "sequence", new(1, child));

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph, draft.SchemaVersion);
        Assert.Equal(3, draft.Nodes.Single(pair => pair.Value.Type == NodeTypes.Sequence).Value.Sequence!.Outputs.Length);
        var call = Assert.Single(draft.Nodes, pair => pair.Value.Type == NodeTypes.WorkflowCall);
        Assert.Equal(JsonBaseline("json-child").WorkflowId, call.Value.WorkflowCall!.WorkflowId);
        Assert.Equal(WorkflowDataTypes.Json, draft.Nodes["output"].OutputDataType);
        Assert.Contains(draft.Connections.Values, edge => edge.Source == new PortReference(call.Key, "out") &&
            edge.Target == new PortReference("output", "in"));
        Assert.Empty(WorkflowValidator.Validate(draft));
    }

    [Fact]
    public void JSON_path_authoring_rejects_a_string_child_or_existing_post_call_nodes()
    {
        var source = PathGraph() with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph };
        var stringChild = WorkflowRevision.Create(Baseline("string-child"), "test");
        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(source, "sequence", new(1, stringChild)));

        var jsonChild = WorkflowRevision.Create(JsonBaseline("json-child"), "test");
        var withAgent = source with { Nodes = source.Nodes.Add("agent", new(NodeTypes.Agent, "1.0.0")) };
        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(withAgent, "sequence", new(1, jsonChild)));
    }

    [Fact]
    public void Adds_one_typed_json_composition_node_after_a_direct_json_call()
    {
        var child = WorkflowRevision.Create(JsonBaseline("json-child"), "test");
        var source = WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(
            PathGraph() with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph }, "sequence", new(1, child));
        var callId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key;

        var selector = WorkflowCallEditing.AppendSequenceWorkflowCallJsonCompositionDraft(
            source, callId, NodeTypes.JsonSelectString, "/answer");

        Assert.Equal(WorkflowDataTypes.String, selector.Nodes["output"].OutputDataType);
        var selectorNode = Assert.Single(selector.Nodes, pair => pair.Value.Type == NodeTypes.JsonSelectString);
        Assert.Equal("/answer", selectorNode.Value.JsonPropertySelection!.Pointer);
        Assert.Empty(WorkflowValidator.Validate(selector));
        Assert.True(SequenceWorkflowCallJsonTopology.Analyze(selector).IsValid);

        var projection = WorkflowCallEditing.AppendSequenceWorkflowCallJsonCompositionDraft(
            source, callId, NodeTypes.JsonProject, "/answer");

        Assert.Equal(WorkflowDataTypes.Json, projection.Nodes["output"].OutputDataType);
        var projectionNode = Assert.Single(projection.Nodes, pair => pair.Value.Type == NodeTypes.JsonProject);
        Assert.Equal("/answer", projectionNode.Value.JsonProjection!.Fields.Single().Pointer);
        Assert.Empty(WorkflowValidator.Validate(projection));
        Assert.True(SequenceWorkflowCallJsonTopology.Analyze(projection).IsValid);
    }

    [Fact]
    public void Adds_a_terminal_selector_after_a_projection_in_schema_1_54()
    {
        var child = WorkflowRevision.Create(JsonBaseline("json-child"), "test");
        var direct = WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(
            PathGraph() with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph }, "sequence", new(1, child));
        var oneNode = WorkflowCallEditing.AppendSequenceWorkflowCallJsonCompositionDraft(
            direct, direct.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key, NodeTypes.JsonProject, "/answer");
        var chain = GraphEditing.EnableSequenceWorkflowCallJsonChainGraph(oneNode);
        var callId = chain.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key;

        var updated = WorkflowCallEditing.AppendSequenceWorkflowCallJsonChainCompositionDraft(
            chain, callId, NodeTypes.JsonSelectString, "/value");

        Assert.Equal(WorkflowDataTypes.String, updated.Nodes["output"].OutputDataType);
        Assert.Equal(2, SequenceWorkflowCallJsonChainTopology.Analyze(updated).Paths.Single().JsonNodeIds.Length);
        Assert.Empty(WorkflowValidator.Validate(updated));
    }

    [Fact]
    public void Adds_a_bounded_object_merge_after_a_projection_in_schema_1_55()
    {
        var child = WorkflowRevision.Create(JsonBaseline("json-child"), "test");
        var direct = WorkflowCallEditing.AppendSequenceWorkflowCallJsonDraft(
            PathGraph() with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph }, "sequence", new(1, child));
        var projected = WorkflowCallEditing.AppendSequenceWorkflowCallJsonCompositionDraft(
            direct, direct.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key, NodeTypes.JsonProject, "/answer");
        var chain = GraphEditing.EnableSequenceWorkflowCallJsonChainGraph(projected);
        var callId = chain.Nodes.Single(pair => pair.Value.Type == NodeTypes.WorkflowCall).Key;

        var merged = WorkflowCallEditing.AppendSequenceWorkflowCallJsonMergeDraft(chain, callId);

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph, merged.SchemaVersion);
        var mergeId = Assert.Single(merged.Nodes, pair => pair.Value.Type == NodeTypes.JsonMerge).Key;
        var projectId = Assert.Single(merged.Nodes, pair => pair.Value.Type == NodeTypes.JsonProject).Key;
        Assert.Contains(merged.Connections.Values, edge => edge.Source == new PortReference(callId, "out") && edge.Target == new PortReference(mergeId, "left"));
        Assert.Contains(merged.Connections.Values, edge => edge.Source == new PortReference(callId, "out") && edge.Target == new PortReference(projectId, "in"));
        Assert.Contains(merged.Connections.Values, edge => edge.Source == new PortReference(mergeId, "out") && edge.Target == new PortReference("output", "in"));
        Assert.Empty(WorkflowValidator.Validate(merged));
        Assert.True(SequenceWorkflowCallJsonMergeTopology.Analyze(merged).IsValid);
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

    private static WorkflowDefinition JsonBaseline(string workflowId) => new("1.13", workflowId, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("edge", new(new("input", "out"), new("output", "in"), "data")));
}
