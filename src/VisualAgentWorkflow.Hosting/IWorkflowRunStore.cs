using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Local synchronous persistence boundary. Hosts own lifecycle, access policy and exclusive runtime ownership.
/// A batch atomically publishes events and their derived run state; expectedSequence is the last committed sequence.
/// Reads return a consistent snapshot. A failed validation/conflict publishes nothing. No retry or execution is implied.</summary>
public interface IWorkflowRunStore
{
    RunSnapshot Create(WorkflowRevision revision, string input);
    RunSnapshot? Get(Guid runId);
    IReadOnlyList<RunSnapshot> List();
    RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events);
}

/// <summary>The store changed after the caller's snapshot. Never blindly retry external operations.</summary>
public sealed class WorkflowRunStoreConflictException() : InvalidOperationException("The run store sequence has changed.");

public static class WorkflowRunStoreExtensions
{
    public static void Append(this IWorkflowRunStore store, ExecutionEvent evt) =>
        store.Append(evt.RunId, evt.Sequence - 1, [evt]);

    /// <summary>Only an exclusive local runtime owner may interrupt abandoned runs. This is not recovery or a lease.</summary>
    public static void MarkUnfinishedAsInterrupted(this IWorkflowRunStore store)
    {
        foreach (var snapshot in store.List().Where(r => !r.IsTerminal))
            store.Append(new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 1,
                DateTimeOffset.UtcNow, ExecutionEventKinds.Interrupted,
                Data: "The runtime stopped before recording a terminal outcome. This run was not resumed."));
    }
}
