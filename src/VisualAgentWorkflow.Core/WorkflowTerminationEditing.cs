namespace VisualAgentWorkflow.Core;

/// <summary>Configures the explicit outcome when a workflow path reaches a termination node.</summary>
public static class WorkflowTerminationEditing
{
    public static WorkflowDefinition SetOutcome(WorkflowDefinition definition, string nodeId, string outcome)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Nodes.TryGetValue(nodeId, out var node) || node.Type != NodeTypes.Terminate)
            throw new ArgumentException("Choose a workflow termination node.", nameof(nodeId));
        if (outcome is not (WorkflowTerminationOutcomes.Success or WorkflowTerminationOutcomes.Failure))
            throw new ArgumentException("Choose success or failure as the termination outcome.", nameof(outcome));

        var updated = definition with
        {
            SchemaVersion = definition.SchemaVersion is "1.22" or "1.21" ? definition.SchemaVersion : "1.20",
            Nodes = definition.Nodes.SetItem(nodeId, node with { Termination = new(outcome) })
        };
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }
}
