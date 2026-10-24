using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Single-process local evidence reader; never invokes a workflow or loads providers.</summary>
public sealed class JournalRunObserver(FileRunJournal journal) : IWorkflowRunObserver
{
    public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RunSummary> summaries = journal.List().Select(RunSummary.From)
            .OrderByDescending(r => r.LastEventAt).ThenBy(r => r.RunId).Take(50).ToArray();
        return Task.FromResult(summaries);
    }

    public Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(journal.Get(runId));
    }

    public Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (afterSequence < 0 || limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var snapshot = journal.Get(runId);
        if (snapshot is null) return Task.FromResult<RunEventPage?>(null);
        if (afterSequence > snapshot.Events.Length) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var events = snapshot.Events.Skip((int)afterSequence).Take(limit).ToImmutableArray();
        return Task.FromResult<RunEventPage?>(new(runId, snapshot.Header.RevisionId, afterSequence, snapshot.Events.Length,
            events, afterSequence + events.Length < snapshot.Events.Length));
    }
}
