namespace VisualAgentWorkflow.Core;

/// <summary>One child-workflow call reconstructed from recorded lifecycle evidence.</summary>
public sealed record WorkflowCallPlaybackCall(
    string CallNodeId,
    string ChildScopeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash,
    string State,
    string? ChildData);

/// <summary>Passive nested-call playback projection. It never executes a child workflow.</summary>
public sealed record WorkflowCallPlaybackSnapshot(
    string Status,
    IReadOnlyList<WorkflowCallPlaybackCall> Calls);

public static class WorkflowCallEvidence
{
    public static bool TryReconstructPlayback(IEnumerable<ExecutionEvent> executionEvents,
        out WorkflowCallPlaybackSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(executionEvents);
        snapshot = null;
        var events = executionEvents.OrderBy(evt => evt.Sequence).ToArray();
        var calls = new Dictionary<string, MutableCall>(StringComparer.Ordinal);
        var rootTerminal = (string?)null;

        foreach (var executionEvent in events)
        {
            if (executionEvent.Kind == ExecutionEventKinds.SubworkflowEvidence)
            {
                // Once the run has reached its root terminal no further nested evidence may appear: a new child start
                // or a duplicated terminal after the terminal is tampering, and the write-time ordering rule
                // (evidence only before the terminal) must hold at read time as well.
                if (rootTerminal is not null) return false;
                if (!WorkflowCallLifecycleEvidence.TryRead(executionEvent.Data, out var evidence) || evidence is null ||
                    executionEvent.ScopeId != evidence.ChildScopeId || executionEvent.NodeId != evidence.CallNodeId)
                    return false;
                if (!calls.TryGetValue(evidence.ChildScopeId, out var call))
                {
                    if (evidence.Lifecycle != ExecutionEventKinds.SubworkflowStarted) return false;
                    call = new(evidence.CallNodeId, evidence.ChildScopeId, evidence.ChildWorkflowId,
                        evidence.ChildRevisionId, evidence.ChildContentHash);
                    calls.Add(evidence.ChildScopeId, call);
                }
                else if (call.CallNodeId != evidence.CallNodeId || call.ChildWorkflowId != evidence.ChildWorkflowId ||
                    call.ChildRevisionId != evidence.ChildRevisionId || call.ChildContentHash != evidence.ChildContentHash)
                    return false;

                if (evidence.Lifecycle == ExecutionEventKinds.SubworkflowStarted)
                {
                    if (call.Started) return false;
                    call.Started = true;
                    call.State = "running";
                }
                else
                {
                    if (!call.Started || call.Terminal) return false;
                    call.Terminal = true;
                    call.State = evidence.Lifecycle switch
                    {
                        ExecutionEventKinds.SubworkflowCompleted => "completed",
                        ExecutionEventKinds.SubworkflowTerminated => "terminated",
                        ExecutionEventKinds.SubworkflowFailed => "failed",
                        ExecutionEventKinds.SubworkflowCancelled => "cancelled",
                        ExecutionEventKinds.SubworkflowOutcomeUnknown => "unknown",
                        _ => throw new InvalidOperationException("Unsupported child lifecycle evidence.")
                    };
                    call.ChildData = evidence.ChildData;
                }
                continue;
            }

            if (executionEvent.ScopeId is not null) continue;
            if (executionEvent.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Terminated or
                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted)
            {
                if (rootTerminal is not null) return false;
                // An interruption is a store-level process-death fact, not an execution outcome: child scopes may
                // legitimately still be in flight when the owner appends it, so only execution terminals require every
                // call to have settled. Calls keep their last-known state and are never upgraded to terminal.
                if (executionEvent.Kind != ExecutionEventKinds.Interrupted && calls.Values.Any(call => !call.Terminal)) return false;
                rootTerminal = executionEvent.Kind switch
                {
                    ExecutionEventKinds.Completed => calls.Values.Any(call => call.State == "unknown") ? null : "completed",
                    ExecutionEventKinds.Terminated => calls.Values.Any(call => call.State == "unknown") ? null : "terminated",
                    ExecutionEventKinds.Failed => "failed",
                    ExecutionEventKinds.Cancelled => "cancelled",
                    ExecutionEventKinds.Interrupted => "interrupted",
                    _ => null
                };
                if (rootTerminal is null) return false;
            }
        }

        if (calls.Count == 0) return false;
        snapshot = new(rootTerminal ?? "running", calls.Values.Select(call => call.ToSnapshot()).ToArray());
        return true;
    }

    private sealed class MutableCall(string callNodeId, string childScopeId, string childWorkflowId,
        Guid childRevisionId, string childContentHash)
    {
        public string CallNodeId { get; } = callNodeId;
        public string ChildScopeId { get; } = childScopeId;
        public string ChildWorkflowId { get; } = childWorkflowId;
        public Guid ChildRevisionId { get; } = childRevisionId;
        public string ChildContentHash { get; } = childContentHash;
        public bool Started { get; set; }
        public bool Terminal { get; set; }
        public string State { get; set; } = "declared";
        public string? ChildData { get; set; }

        public WorkflowCallPlaybackCall ToSnapshot() => new(CallNodeId, ChildScopeId, ChildWorkflowId,
            ChildRevisionId, ChildContentHash, State, ChildData);
    }
}
