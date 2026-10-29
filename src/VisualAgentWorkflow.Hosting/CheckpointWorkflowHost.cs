using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Host-owned checkpoint capabilities. Loop recovery is experimental and disabled by default.</summary>
public sealed record CheckpointWorkflowHostOptions(bool EnableLoopCheckpointRecovery = false);

/// <summary>Opt-in explicit deterministic checkpoint execution. Not the Studio queue, automatic failover or external-call recovery.</summary>
public sealed class CheckpointWorkflowHost : IAsyncDisposable
{
    private readonly IWorkflowCheckpointHostStore store;
    private readonly IDisposable ownership;
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CheckpointWorkflowHostOptions options;
    private bool disposed;

    public CheckpointWorkflowHost(IWorkflowCheckpointHostStore store, CheckpointWorkflowHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.options = options ?? new();
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
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            await execution.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CheckpointWorkflowHost));
        }
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
                WorkflowRunner.ValidateCheckpointGraph(revision.Definition, options.EnableLoopCheckpointRecovery);
                snapshot = store.Create(revision, input);
            }
            var sink = new Sink(store, snapshot, revision!, restored?.Native, suspendAfterCompletedNodes, options.EnableLoopCheckpointRecovery);
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

    private sealed class Sink(IWorkflowCheckpointHostStore store, RunSnapshot initial, WorkflowRevision revision,
        NativeWorkflowCheckpoint? restored, int? suspendAfter, bool allowLoopCheckpointRecovery) : IWorkflowCheckpointSink, IWorkflowCheckpointBoundarySink
    {
        private RunSnapshot snapshot = initial;
        private (NativeWorkflowCheckpoint Checkpoint, CheckpointBoundaryObservation Observation)? boundary;
        public NativeWorkflowCheckpoint? RestoredCheckpoint => restored;
        public bool AllowLoopCheckpointRecovery => allowLoopCheckpointRecovery;

        public void ObserveBoundary(NativeWorkflowCheckpoint checkpoint, CheckpointBoundaryObservation observation) =>
            boundary = (checkpoint, observation);

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
            if (revision.Definition.SchemaVersion == WorkflowSchemaVersions.LoopVariablesGraph &&
                !snapshot.Events.Any(evt => evt.NodeId is not null &&
                    revision.Definition.Nodes.TryGetValue(evt.NodeId, out var node) && node.Type == NodeTypes.Loop &&
                    evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped))
            {
                boundary = null;
                return ValueTask.FromResult(false);
            }
            if (revision.Definition.SchemaVersion == WorkflowSchemaVersions.LoopVariablesGraph &&
                boundary is { Observation.NextNodeId: null })
            {
                boundary = null;
                return ValueTask.FromResult(false);
            }
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
            if (revision.Definition.SchemaVersion == WorkflowSchemaVersions.LoopVariablesGraph)
            {
                var observed = boundary ?? throw new InvalidOperationException("The loop checkpoint boundary was not captured by the host.");
                if (!string.Equals(observed.Checkpoint.CheckpointId, native.CheckpointId, StringComparison.Ordinal) ||
                    !string.Equals(observed.Checkpoint.Payload, native.Payload, StringComparison.Ordinal))
                    throw new InvalidOperationException("The loop checkpoint boundary does not match the native checkpoint being committed.");
                var loopId = revision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
                var bodyId = revision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
                var loopEvent = snapshot.Events.LastOrDefault(evt => evt.NodeId == loopId &&
                    evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
                    ?? throw new InvalidOperationException("The loop checkpoint boundary has no recorded loop evidence.");
                var cursor = new LoopCheckpointCursor(loopId, bodyId, observed.Observation.LoopIteration,
                    revision.Definition.Nodes[loopId].Loop!.MaximumIterations,
                    observed.Observation.NextNodeId ?? throw new InvalidOperationException("The loop checkpoint boundary has no next node."),
                    observed.Observation.NextInputPortId ?? throw new InvalidOperationException("The loop checkpoint boundary has no next input port."),
                    observed.Observation.SourceNodeId, observed.Observation.SourcePortId, loopEvent.Sequence);
                var evidence = snapshot.Events.Concat(events).ToArray();
                checkpoint = checkpoint with
                {
                    Frame = WorkflowCheckpointFrame.Create(revision, snapshot.Header.RunId, native.Payload, cursor,
                        native.VariableState ?? throw new InvalidOperationException("The loop checkpoint has no variable snapshot."), evidence)
                };
            }
            try
            {
                snapshot = store.CommitCheckpoint(snapshot.Header.RunId, snapshot.Events.Length, events, checkpoint);
            }
            catch (Exception error) when (error is not (OperationCanceledException or
                OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // A durable commit can succeed while its acknowledgement is lost.
                // Re-read and adopt only the exact suspended native state; otherwise
                // preserve the original failure and never guess at recovery.
                try
                {
                    var persisted = store.Get(snapshot.Header.RunId);
                    var persistedCheckpoint = persisted is null ? null : store.GetCheckpoint(snapshot.Header.RunId);
                    if (persisted is null || !WorkflowCheckpointRecovery.IsCommittedSuspension(persisted, persistedCheckpoint, native))
                        throw new InvalidOperationException("Checkpoint publication failed and no committed suspension was found.", error);
                    snapshot = persisted;
                }
                catch (Exception adoptionError) when (adoptionError is not (OperationCanceledException or
                    OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    throw new InvalidOperationException("Checkpoint publication acknowledgement could not be reconciled.", error);
                }
            }
            boundary = null;
            return ValueTask.FromResult(suspended);
        }
    }
}
