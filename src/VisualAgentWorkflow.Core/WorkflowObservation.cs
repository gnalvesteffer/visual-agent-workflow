using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Payload-free discovery metadata. LastEventAt is activity time, not revision publication time.</summary>
public sealed record RunSummary(Guid RunId, Guid RevisionId, string WorkflowId, string ContentHash,
    string Status, long LastSequence, DateTimeOffset? LastEventAt)
{
    public static RunSummary From(RunSnapshot snapshot) => new(snapshot.Header.RunId, snapshot.Header.RevisionId,
        snapshot.Header.Definition.WorkflowId, snapshot.Header.ContentHash, snapshot.Status,
        snapshot.Events.Length, snapshot.Events.LastOrDefault()?.Timestamp);
}

public sealed record RunEventPage(Guid RunId, Guid RevisionId, long AfterSequence, long LastSequence,
    ImmutableArray<ExecutionEvent> Events, bool HasMore);

/// <summary>Read-only evidence access. Hosts own identity, authorization, filtering, storage and transport.</summary>
public interface IWorkflowRunObserver
{
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default);
    Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default);
}
