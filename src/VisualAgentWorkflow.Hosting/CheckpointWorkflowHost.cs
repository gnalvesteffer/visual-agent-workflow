using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Opt-in explicit deterministic checkpoint execution. Not the Studio queue, automatic failover or external-call recovery.</summary>
public sealed class CheckpointWorkflowHost : IAsyncDisposable
{
    private readonly SqliteWorkflowRunStore store;
    private readonly IDisposable ownership;
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public CheckpointWorkflowHost(SqliteWorkflowRunStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        ownership = store.AcquireRuntimeOwnership();
    }

    public Task<RunSnapshot> StartAsync(WorkflowRevision revision, string input, int? suspendAfterCompletedNodes = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(null, null, revision, input, suspendAfterCompletedNodes, cancellationToken);

    public Task<RunSnapshot> ResumeAsync(Guid runId, string expectedCheckpointId, int? suspendAfterCompletedNodes = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCheckpointId);
        return ExecuteAsync(runId, expectedCheckpointId, null, null, suspendAfterCompletedNodes, cancellationToken);
    }

    private async Task<RunSnapshot> ExecuteAsync(Guid? runId, string? expectedCheckpointId, WorkflowRevision? revision, string? input, int? suspendAfterCompletedNodes, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        await execution.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (suspendAfterCompletedNodes is <= 0) throw new ArgumentOutOfRangeException(nameof(suspendAfterCompletedNodes));
            RunSnapshot snapshot;
            WorkflowRunCheckpoint? restored = null;
            if (runId is { } id)
            {
                snapshot = store.Get(id) ?? throw new KeyNotFoundException("Unknown run.");
                restored = store.GetCheckpoint(id);
                if (restored?.Native.CheckpointId != expectedCheckpointId) throw new WorkflowRunStoreConflictException();
                var assessment = WorkflowCheckpointRecovery.Assess(snapshot, restored);
                if (!assessment.CanResume) throw new InvalidOperationException(assessment.Reason);
                revision = WorkflowRevision.Rehydrate(snapshot.Header.RevisionId, snapshot.Header.Definition, snapshot.Header.Author,
                    snapshot.Header.ParentRevisionId, snapshot.Header.CreatedAt, snapshot.Header.ContentHash);
                input = snapshot.Header.Input;
                if (suspendAfterCompletedNodes <= snapshot.Events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted))
                    throw new ArgumentException("The next suspension threshold must advance completed work.", nameof(suspendAfterCompletedNodes));
            }
            else
            {
                ArgumentNullException.ThrowIfNull(revision);
                ArgumentNullException.ThrowIfNull(input);
                WorkflowRunner.ValidateCheckpointGraph(revision.Definition);
                snapshot = store.Create(revision, input);
            }
            var sink = new Sink(store, snapshot, restored?.Native, suspendAfterCompletedNodes);
            await new WorkflowRunner().ExecuteCheckpointedAsync(snapshot.Header.RunId, revision!, input!, sink.RecordAsync, sink, linked.Token);
            return store.Get(snapshot.Header.RunId)!;
        }
        finally { execution.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await execution.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            ownership.Dispose();
        }
        finally { execution.Release(); }
        // The cancellation source/semaphore remain valid for callers already queued during disposal.
    }

    private sealed class Sink(SqliteWorkflowRunStore store, RunSnapshot initial, NativeWorkflowCheckpoint? restored, int? suspendAfter) : IWorkflowCheckpointSink
    {
        private RunSnapshot snapshot = initial;
        public NativeWorkflowCheckpoint? RestoredCheckpoint => restored;

        public ValueTask RecordAsync(ExecutionEvent evt)
        {
            snapshot = store.Append(snapshot.Header.RunId, snapshot.Events.Length, [evt with { Sequence = snapshot.Events.Length + 1 }]);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint native)
        {
            var completed = snapshot.Events.Where(e => e.Kind == ExecutionEventKinds.NodeCompleted).ToArray();
            var suspended = suspendAfter is { } count && completed.Length >= count &&
                !completed.Any(e => snapshot.Header.Definition.Nodes[e.NodeId!].Type == NodeTypes.Output);
            var hash = WorkflowRunCheckpoint.Hash(native.Payload);
            var metadata = JsonSerializer.Serialize(new { native.CheckpointId, payloadHash = hash, runtimeVersion = WorkflowRunner.CheckpointRuntimeVersion });
            var events = new List<ExecutionEvent>
            {
                new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 1, DateTimeOffset.UtcNow,
                    ExecutionEventKinds.CheckpointCaptured, Data: metadata)
            };
            if (suspended) events.Add(new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 2,
                DateTimeOffset.UtcNow, ExecutionEventKinds.Suspended, Data: "Saved deterministic boundary; explicit host recovery required."));
            var checkpoint = new WorkflowRunCheckpoint("1.0", WorkflowRunner.CheckpointRuntimeVersion, snapshot.Header.RunId,
                snapshot.Header.RevisionId, snapshot.Header.ContentHash, snapshot.Events.Length + events.Count, native, hash)
            {
                VariableStateHash = native.VariableState?.ComputeHash()
            };
            snapshot = store.CommitCheckpoint(snapshot.Header.RunId, snapshot.Events.Length, events, checkpoint);
            return ValueTask.FromResult(suspended);
        }
    }
}
