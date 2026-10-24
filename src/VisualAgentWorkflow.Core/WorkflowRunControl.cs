namespace VisualAgentWorkflow.Core;

/// <summary>Optional host capability. Commands request cooperative pauses, never rollback of in-flight work.</summary>
public interface IWorkflowRunControl
{
    Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input);
    Task PauseRunAsync(Guid runId);
    Task ResumeRunAsync(Guid runId);
}

public interface IPublishedWorkflowRunControl : IWorkflowRunControl
{
    Task<RunSnapshot> StartPublishedPausedRunAsync(Guid revisionId, string input);
}

/// <summary>Trusted host gate before a node is admitted, and before final completion (null node ID).
/// Cancellation must unblock waits. Implementations must not execute or reconstruct workflow nodes.</summary>
public interface IWorkflowExecutionGate
{
    ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken);
}

public sealed class WorkflowRunControlException()
    : InvalidOperationException("The run is terminal or its execution is no longer owned by this runtime.");
