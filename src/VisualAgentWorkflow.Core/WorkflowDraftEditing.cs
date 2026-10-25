namespace VisualAgentWorkflow.Core;

/// <summary>Validation boundary for local configuration edits made while graph topology is incomplete.</summary>
internal static class WorkflowDraftEditing
{
    private static readonly HashSet<string> IncompleteTopologyIssues = new(StringComparer.Ordinal)
    {
        "pipeline.degree", "graph.degree", "graph.unreachable", "graph.cycle", "data.degree", "data.unused"
    };

    public static bool HasOnlyIncompleteTopologyIssues(WorkflowDefinition definition)
    {
        var issues = WorkflowValidator.Validate(definition);
        return !issues.IsEmpty && issues.All(issue => IncompleteTopologyIssues.Contains(issue.Code));
    }

    public static WorkflowDefinition ValidateEdit(WorkflowDefinition source, WorkflowDefinition edited)
    {
        if (HasOnlyIncompleteTopologyIssues(source)) WorkflowJson.SerializeDraft(edited);
        else WorkflowValidator.ThrowIfInvalid(edited);
        return edited;
    }
}
