using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Replayable identity evidence for a nested workflow lifecycle event.</summary>
public sealed record WorkflowCallLifecycleEvidence(
    string Lifecycle,
    string CallNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash,
    string ChildScopeId,
    string? ChildData)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static bool TryRead(string? data, out WorkflowCallLifecycleEvidence? evidence)
    {
        try
        {
            evidence = string.IsNullOrWhiteSpace(data)
                ? null
                : JsonSerializer.Deserialize<WorkflowCallLifecycleEvidence>(data);
            return evidence is not null;
        }
        catch (JsonException)
        {
            evidence = null;
            return false;
        }
    }
}
