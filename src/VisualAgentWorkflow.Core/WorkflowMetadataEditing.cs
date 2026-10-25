namespace VisualAgentWorkflow.Core;

/// <summary>Edits user-facing workflow metadata while preserving its stable identity.</summary>
public static class WorkflowMetadataEditing
{
    public const int MaximumDisplayNameLength = 128;

    public static WorkflowDefinition Rename(WorkflowDefinition definition, string displayName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(displayName);

        var normalizedName = displayName.Trim();
        if (normalizedName.Length is 0 or > MaximumDisplayNameLength || normalizedName.Any(char.IsControl))
            throw new ArgumentException($"Workflow names must contain 1 to {MaximumDisplayNameLength} printable characters.", nameof(displayName));

        return definition with { DisplayName = normalizedName };
    }
}
