using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record RunHeader(Guid RunId, Guid RevisionId, Guid? ParentRevisionId, string Author,
    DateTimeOffset CreatedAt, string ContentHash, WorkflowDefinition Definition, string Input);

public sealed record RunSnapshot(RunHeader Header, ImmutableArray<ExecutionEvent> Events)
{
    public string Status
    {
        get
        {
            var terminal = Events.LastOrDefault(e => e.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted);
            if (terminal is not null) return terminal.Kind switch
            {
                ExecutionEventKinds.Completed => "completed",
                ExecutionEventKinds.Failed => "failed",
                ExecutionEventKinds.Cancelled => "cancelled",
                _ => "interrupted"
            };
            // Pause and approval are independent holds. Resume cannot approve, and approval cannot resume.
            var pause = Events.LastOrDefault(e => e.Kind is ExecutionEventKinds.PauseRequested or ExecutionEventKinds.Paused or ExecutionEventKinds.Resumed);
            if (pause?.Kind == ExecutionEventKinds.Paused) return "paused";
            if (pause?.Kind == ExecutionEventKinds.PauseRequested) return "pausing";
            if (Events.LastOrDefault(e => WorkflowApprovalEvidence.IsTransition(e.Kind))?.Kind == ExecutionEventKinds.ApprovalRequested)
                return "waiting-approval";
            return Events.Any(e => e.Kind is not (ExecutionEventKinds.PauseRequested or ExecutionEventKinds.Paused or ExecutionEventKinds.Resumed))
                ? "running" : "queued";
        }
    }

    public bool IsTerminal => Status is "completed" or "failed" or "cancelled" or "interrupted";
}

/// <summary>Host-supplied access to execution. Implementations own authorization and run lifetimes.</summary>
public interface IWorkflowClient
{
    Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input);
    Task<RunSnapshot?> GetRunAsync(Guid runId);
    Task<IReadOnlyList<RunSnapshot>> ListRunsAsync();
    Task CancelRunAsync(Guid runId);
}
