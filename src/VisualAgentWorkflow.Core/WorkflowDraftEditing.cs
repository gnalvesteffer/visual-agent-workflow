namespace VisualAgentWorkflow.Core;

/// <summary>Validation boundary for local configuration edits made while graph topology is incomplete.</summary>
internal static class WorkflowDraftEditing
{
    public static bool HasOnlyDraftableIssues(WorkflowDefinition definition) => WorkflowJson.HasOnlyDraftableIssues(definition);

    public static WorkflowDefinition ValidateEdit(WorkflowDefinition edited)
    {
        WorkflowJson.SerializeDraft(edited);
        return edited;
    }
}
