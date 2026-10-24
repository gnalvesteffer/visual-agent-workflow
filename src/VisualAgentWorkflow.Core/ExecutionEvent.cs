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
    string? Data = null);

public static class ExecutionEventKinds
{
    public const string Started = "run.started";
    public const string NodeStarted = "node.started";
    public const string NodeCompleted = "node.completed";
    public const string Transfer = "connection.received";
    public const string Completed = "run.completed";
    public const string Failed = "run.failed";
    public const string Cancelled = "run.cancelled";
    public const string Interrupted = "run.interrupted";
}
