using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowCallUpgradeResult(WorkflowDefinition Definition,
    ImmutableArray<WorkflowCallParameterMapping> DroppedParameterMappings);

/// <summary>Explicit edits for immutable, revision-pinned workflow call nodes.</summary>
public static class WorkflowCallEditing
{
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
}
