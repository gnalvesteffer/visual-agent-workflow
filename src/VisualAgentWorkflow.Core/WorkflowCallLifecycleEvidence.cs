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
    private static readonly HashSet<string> Lifecycles = new(StringComparer.Ordinal)
    {
        ExecutionEventKinds.SubworkflowStarted,
        ExecutionEventKinds.SubworkflowCompleted,
        ExecutionEventKinds.SubworkflowTerminated,
        ExecutionEventKinds.SubworkflowFailed,
        ExecutionEventKinds.SubworkflowCancelled,
        ExecutionEventKinds.SubworkflowOutcomeUnknown
    };

    public string Serialize() => JsonSerializer.Serialize(this);

    public static bool TryRead(string? data, out WorkflowCallLifecycleEvidence? evidence)
    {
        try
        {
            evidence = string.IsNullOrWhiteSpace(data)
                ? null
                : JsonSerializer.Deserialize<WorkflowCallLifecycleEvidence>(data);
            return evidence is not null && Lifecycles.Contains(evidence.Lifecycle) &&
                !string.IsNullOrWhiteSpace(evidence.CallNodeId) &&
                !string.IsNullOrWhiteSpace(evidence.ChildWorkflowId) &&
                evidence.ChildRevisionId != Guid.Empty && IsHash(evidence.ChildContentHash) &&
                !string.IsNullOrWhiteSpace(evidence.ChildScopeId) &&
                (evidence.ChildScopeId == evidence.CallNodeId ||
                    evidence.ChildScopeId.StartsWith(evidence.CallNodeId + "/", StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            evidence = null;
            return false;
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();
}
