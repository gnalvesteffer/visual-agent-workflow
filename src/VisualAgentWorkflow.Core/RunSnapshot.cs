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
            // Model/tool/node evidence can arrive after a pause request. Only an acknowledged
            // pause or later control/terminal transition changes that control state.
            var transition = Events.LastOrDefault(e => e.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted or ExecutionEventKinds.PauseRequested or
                ExecutionEventKinds.Paused or ExecutionEventKinds.Resumed);
            return transition?.Kind switch
            {
                ExecutionEventKinds.Completed => "completed",
                ExecutionEventKinds.Failed => "failed",
                ExecutionEventKinds.Cancelled => "cancelled",
                ExecutionEventKinds.Interrupted => "interrupted",
                ExecutionEventKinds.PauseRequested => "pausing",
                ExecutionEventKinds.Paused => "paused",
                _ => Events.Any(e => e.Kind is not (ExecutionEventKinds.PauseRequested or ExecutionEventKinds.Paused or ExecutionEventKinds.Resumed))
                    ? "running" : "queued"
            };
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
