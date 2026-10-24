using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record RunHeader(Guid RunId, Guid RevisionId, Guid? ParentRevisionId, string Author,
    DateTimeOffset CreatedAt, string ContentHash, WorkflowDefinition Definition, string Input);

public sealed record RunSnapshot(RunHeader Header, ImmutableArray<ExecutionEvent> Events)
{
    public string Status => Events.LastOrDefault()?.Kind switch
    {
        ExecutionEventKinds.Completed => "completed",
        ExecutionEventKinds.Failed => "failed",
        ExecutionEventKinds.Cancelled => "cancelled",
        ExecutionEventKinds.Interrupted => "interrupted",
        null => "queued",
        _ => "running"
    };

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
