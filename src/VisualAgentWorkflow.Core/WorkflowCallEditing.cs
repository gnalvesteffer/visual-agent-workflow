using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowCallUpgradeResult(WorkflowDefinition Definition,
    ImmutableArray<WorkflowCallParameterMapping> DroppedParameterMappings);

/// <summary>Explicit edits for immutable, revision-pinned workflow call nodes.</summary>
public static class WorkflowCallEditing
{
    /// <summary>
    /// Appends a pinned child to one direct-MCP Sequence output path while preserving the
    /// source schema as a saveable draft. The explicit draft-promotion boundary upgrades it
    /// only after the complete schema-1.50 topology validates.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallDraft(WorkflowDefinition parent,
        string sequenceNodeId, string outputId, PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        if (parent.SchemaVersion != WorkflowSchemaVersions.SequenceTransformChainGraph)
            throw new ArgumentException("WorkflowCall authoring requires a schema 1.49 Sequence draft.", nameof(parent));
        if (target.Version < 1 || target.Revision.Definition.WorkflowId == parent.WorkflowId)
            throw new ArgumentException("Choose a different published child workflow revision.", nameof(target));
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);
        if (!HasStringBoundary(target.Revision.Definition))
            throw new ArgumentException("The child workflow must have string input and output boundaries.", nameof(target));

        if (!parent.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is null || !sequence.Sequence.Outputs.Any(output => output.Id == outputId))
            throw new ArgumentException("Choose an existing Sequence output.", nameof(outputId));
        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequencePathTopologyAnalyzer.Analyze(parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.OutputId == outputId);
        if (path is null || !path.IsDirectMcpPath)
            throw new ArgumentException("Choose an output with a valid direct-MCP path. Calls must be terminal and cannot follow transforms or Agents.", nameof(outputId));

        var execution = parent.ExecutionConnections!;
        var outputNode = parent.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (outputNode.Value is null) throw new ArgumentException("The Sequence graph has no Output node.", nameof(parent));
        var terminalNodeId = path.NodeIds.IsEmpty ? sequenceNodeId : path.NodeIds[^1];
        var terminalPort = path.NodeIds.IsEmpty ? SequenceOutputLimits.PinId(outputId) : "then";
        var existingEdge = execution.SingleOrDefault(edge => edge.Value.Source.NodeId == terminalNodeId &&
            edge.Value.Source.PortId == terminalPort && edge.Value.Target.NodeId == outputNode.Key && edge.Value.Target.PortId == "execute");
        if (existingEdge.Value is null)
            throw new ArgumentException("The selected Sequence output must have exactly one direct path to Output.", nameof(outputId));

        var callId = NewId("workflow-call");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(target.Revision.Definition.WorkflowId, target.Revision.RevisionId, target.Version,
                target.Revision.ContentHash, target.Revision.Definition)
        };
        var updatedExecution = execution
            .SetItem(existingEdge.Key, existingEdge.Value with { Target = new(callId, "execute") })
            .Add(NewId("workflow-call"), new(new(callId, "out"), new(outputNode.Key, "execute"), "execution"));
        var updated = parent with
        {
            Nodes = parent.Nodes.Add(callId, call),
            ExecutionConnections = updatedExecution
        };
        // This intentionally validates only the editor draft contract. Strict validation is
        // reserved for WorkflowDraftPromotion, which is the only path to schema 1.50.
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>Updates a pinned call in a schema-1.49 call-bearing draft without changing its node ID.</summary>
    public static WorkflowCallUpgradeResult UpgradeSequenceWorkflowCallDraft(WorkflowDefinition parent,
        string nodeId, PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        if (parent.SchemaVersion != WorkflowSchemaVersions.SequenceTransformChainGraph)
            throw new ArgumentException("WorkflowCall authoring requires a schema 1.49 Sequence draft.", nameof(parent));
        if (!parent.Nodes.TryGetValue(nodeId, out var node) || node.Type != NodeTypes.WorkflowCall || node.WorkflowCall is not { } call)
            throw new ArgumentException("Choose an existing WorkflowCall node.", nameof(nodeId));
        if (call.WorkflowId != target.Revision.Definition.WorkflowId || target.Version < 1)
            throw new ArgumentException("The replacement must be another revision of the same child workflow.", nameof(target));
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);
        if (!HasStringBoundary(target.Revision.Definition))
            throw new ArgumentException("The child workflow must have string input and output boundaries.", nameof(target));

        var parentParameters = parent.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var childParameters = target.Revision.Definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var existingMappings = call.ParameterMappings ?? [];
        var retainedMappings = existingMappings.Where(mapping =>
            parentParameters.TryGetValue(mapping.ParentParameterId, out var parentParameter) &&
            childParameters.TryGetValue(mapping.ChildParameterId, out var childParameter) &&
            WorkflowParameters.AreCompatible(parentParameter, childParameter)).ToImmutableArray();
        var droppedMappings = existingMappings.Except(retainedMappings).ToImmutableArray();
        var updatedCall = call with
        {
            RevisionId = target.Revision.RevisionId,
            Version = target.Version,
            ContentHash = target.Revision.ContentHash,
            Definition = target.Revision.Definition,
            ParameterMappings = retainedMappings.IsEmpty ? null : retainedMappings
        };
        var updated = parent with { Nodes = parent.Nodes.SetItem(nodeId, node with { WorkflowCall = updatedCall }) };
        WorkflowJson.SerializeDraft(updated);
        return new(updated, droppedMappings);
    }

    public static WorkflowCallUpgradeResult Upgrade(WorkflowDefinition parent, string nodeId,
        PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        WorkflowValidator.ThrowIfInvalid(parent);
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);

        if (target.Version < 1) throw new ArgumentException("The target publication version is invalid.", nameof(target));
        if (!parent.Nodes.TryGetValue(nodeId, out var node) || node.Type != NodeTypes.WorkflowCall || node.WorkflowCall is not { } call)
            throw new ArgumentException("Choose an existing pinned workflow call node.", nameof(nodeId));
        if (call.WorkflowId != target.Revision.Definition.WorkflowId)
            throw new ArgumentException("A pinned call can only be upgraded to another revision of the same workflow.", nameof(target));
        if (call.RevisionId == target.Revision.RevisionId)
            throw new ArgumentException("This revision is already pinned by the selected call.", nameof(target));

        var parentParameters = parent.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var childParameters = target.Revision.Definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var existingMappings = call.ParameterMappings ?? [];
        var retainedMappings = existingMappings.Where(mapping =>
            parentParameters.TryGetValue(mapping.ParentParameterId, out var parentParameter) &&
            childParameters.TryGetValue(mapping.ChildParameterId, out var childParameter) &&
            WorkflowParameters.AreCompatible(parentParameter, childParameter)).ToImmutableArray();
        var droppedMappings = existingMappings.Except(retainedMappings).ToImmutableArray();
        var updatedCall = call with
        {
            RevisionId = target.Revision.RevisionId,
            Version = target.Version,
            ContentHash = target.Revision.ContentHash,
            Definition = target.Revision.Definition,
            ParameterMappings = retainedMappings.IsEmpty ? null : retainedMappings
        };
        var updated = GraphEditing.ReplaceNode(parent, nodeId, node with { WorkflowCall = updatedCall });
        return new(updated, droppedMappings);
    }

    private static bool HasStringBoundary(WorkflowDefinition definition)
    {
        var input = definition.Nodes.GetValueOrDefault(definition.EntryNodeId);
        var output = definition.Nodes.Values.FirstOrDefault(node => node.Type == NodeTypes.Output);
        return input is not null && output is not null &&
            WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType == WorkflowDataTypes.String &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.String;
    }

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
}
