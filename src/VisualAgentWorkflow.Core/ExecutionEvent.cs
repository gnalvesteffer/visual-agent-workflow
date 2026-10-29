namespace VisualAgentWorkflow.Core;

/// <summary>Recorded execution facts. Data is plain text in this initial local-only implementation.</summary>
public sealed record ExecutionEvent(
    Guid RunId,
    Guid RevisionId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Kind,
    string? NodeId = null,
    Guid? InvocationId = null,
    string? ConnectionId = null,
    string? Data = null)
{
    /// <summary>Slash-separated workflow call node IDs; null identifies the root graph.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScopeId { get; init; }
}

public static class ExecutionEventKinds
{
    public const string CheckpointCaptured = "checkpoint.captured";
    public const string Suspended = "run.suspended";
    public const string Recovered = "run.recovered";
    public const string Started = "run.started";
    public const string NodeStarted = "node.started";
    public const string NodeCompleted = "node.completed";
    public const string NodeFailed = "node.failed";
    public const string NodeCancelled = "node.cancelled";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalApproved = "approval.approved";
    public const string ApprovalRejected = "approval.rejected";
    public const string ApprovalCancelled = "approval.cancelled";
    public const string BranchSelected = "branch.selected";
    public const string LoopIterationStarted = "loop.iteration.started";
    public const string LoopIterationCompleted = "loop.iteration.completed";
    public const string LoopStopped = "loop.stopped";
    public const string ParallelJoinProgress = "parallel.join.progress";
    public const string ParallelCollaborationRegionStarted = "parallel.collaboration.region.started";
    public const string ParallelCollaborationMessageSent = "parallel.collaboration.message.sent";
    public const string ParallelCollaborationMessagesRead = "parallel.collaboration.messages.read";
    public const string ParallelCollaborationArrival = "parallel.collaboration.arrival";
    public const string ParallelCollaborationReleased = "parallel.collaboration.released";
    public const string ParallelCollaborationAborted = "parallel.collaboration.aborted";
    public const string Transfer = "connection.received";
    public const string ExecutionActivated = "execution.activated";
    public const string DataEvaluated = "data.evaluated";
    public const string VariableRead = "variable.get";
    public const string VariableWritten = "variable.set";
    public const string Completed = "run.completed";
    public const string Terminated = "run.terminated";
    public const string SubworkflowStarted = "subworkflow.started";
    public const string SubworkflowCompleted = "subworkflow.completed";
    public const string SubworkflowTerminated = "subworkflow.terminated";
    public const string SubworkflowFailed = "subworkflow.failed";
    public const string SubworkflowCancelled = "subworkflow.cancelled";
    public const string SubworkflowEvidence = "subworkflow.evidence";
    public const string Failed = "run.failed";
    public const string Cancelled = "run.cancelled";
    public const string Interrupted = "run.interrupted";
    public const string PauseRequested = "run.pause-requested";
    public const string Paused = "run.paused";
    public const string Resumed = "run.resumed";
    public const string ToolRequested = "tool.requested";
    public const string ToolDenied = "tool.denied";
    public const string ToolAuthorized = "tool.authorized";
    public const string ToolCompleted = "tool.completed";
    public const string ToolFailed = "tool.failed";
    public const string ToolUncertain = "tool.outcome-unknown";
    public const string AgentStarted = "agent.started";
    public const string AgentCompleted = "agent.completed";
    public const string AgentRouteProposed = "agent.route.proposed";
    public const string AgentRouteRejected = "agent.route.rejected";
    public const string AgentRouteCommitted = "agent.route.committed";
    public const string AgentRouteDiscarded = "agent.route.discarded";
    public const string AgentFailed = "agent.failed";
    public const string AgentLimitExceeded = "agent.limit_exceeded";
    public const string AgentOutcome = "agent.outcome";
    public const string AgentTeamStarted = "agent.team.started";
    public const string AgentTeamCompleted = "agent.team.completed";
    public const string AgentTeamFailed = "agent.team.failed";
    public const string AgentChannelMessageSent = "agent.channel.message.sent";
    public const string AgentChannelMessagesRead = "agent.channel.messages.read";
    public const string AgentGraphFunctionStarted = "agent.graph-function.started";
    public const string AgentGraphFunctionCompleted = "agent.graph-function.completed";
    public const string AgentGraphFunctionFailed = "agent.graph-function.failed";
    public const string ModelRequested = "model.requested";
    public const string ModelResponded = "model.responded";
    public const string AgentContextCompacted = "agent.context_compacted";
    public const string SequenceStarted = "sequence.started";
    public const string SequenceOutputDispatched = "sequence.output.dispatched";
    public const string SequenceOutputCompleted = "sequence.output.completed";
    public const string SequenceOutputFailed = "sequence.output.failed";
    public const string SequenceOutputCancelled = "sequence.output.cancelled";
    public const string SequenceOutputSkipped = "sequence.output.skipped";
    public const string SequenceCompleted = "sequence.completed";
    public const string SequenceFailed = "sequence.failed";
    public const string SequenceCancelled = "sequence.cancelled";
}
