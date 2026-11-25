using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Host-owned checkpoint capabilities. Loop and For Each recovery are explicitly opt-in and disabled by default.</summary>
public sealed record CheckpointWorkflowHostOptions(bool EnableLoopCheckpointRecovery = false,
    bool AllowForEachCheckpointRecovery = false);

/// <summary>Opt-in explicit deterministic checkpoint execution. Not the Studio queue, automatic failover or external-call recovery.</summary>
public sealed class CheckpointWorkflowHost : IAsyncDisposable
{
    private readonly IWorkflowCheckpointHostStore store;
    private readonly IDisposable ownership;
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CheckpointWorkflowHostOptions options;
    private readonly TimeProvider clock;
    private bool disposed;

    public CheckpointWorkflowHost(IWorkflowCheckpointHostStore store, CheckpointWorkflowHostOptions? options = null)
        : this(store, options, TimeProvider.System) { }

    /// <summary>Adds a host clock; the parameter is required so existing calls never bind here.</summary>
    public CheckpointWorkflowHost(IWorkflowCheckpointHostStore store, CheckpointWorkflowHostOptions? options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        clock = timeProvider;
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
                WorkflowRunner.ValidateCheckpointGraph(revision.Definition, options.EnableLoopCheckpointRecovery,
                    options.AllowForEachCheckpointRecovery);
                snapshot = store.Create(revision, input);
            }
            var sink = new Sink(store, snapshot, revision!, restored?.Native, suspendAfterCompletedNodes, options.EnableLoopCheckpointRecovery, clock,
                options.AllowForEachCheckpointRecovery);
            await new WorkflowRunner() { Clock = clock }.ExecuteCheckpointedAsync(snapshot.Header.RunId, revision!, input!, sink.RecordAsync, sink, linked.Token);
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
        NativeWorkflowCheckpoint? restored, int? suspendAfter, bool allowLoopCheckpointRecovery, TimeProvider clock,
        bool allowForEachCheckpointRecovery) : IWorkflowCheckpointSink, IWorkflowCheckpointBoundarySink
    {
        private RunSnapshot snapshot = initial;
        private (NativeWorkflowCheckpoint Checkpoint, CheckpointBoundaryObservation Observation)? boundary;
        public NativeWorkflowCheckpoint? RestoredCheckpoint => restored;
        public bool AllowLoopCheckpointRecovery => allowLoopCheckpointRecovery;
        public bool AllowForEachCheckpointRecovery => allowForEachCheckpointRecovery;

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
            var forEachGraph = WorkflowCheckpointShape.UsesForEachState(revision.Definition);
            if (forEachGraph)
            {
                // Only an iteration boundary (a For Each that just started a pass) can be saved, and only when
                // suspending; every other capture is passed over without evidence so the run stays unchanged.
                var forEachId = revision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.ForEach).Key;
                var atBoundary = boundary is { } seen && seen.Observation.CurrentNodeId == forEachId &&
                    seen.Observation.SourcePortId == "body" && seen.Observation.ForEach is not null;
                if (!atBoundary || suspendAfter is not { } threshold || completed.Length < threshold)
                {
                    boundary = null;
                    return ValueTask.FromResult(false);
                }
            }
            var suspended = suspendAfter is { } count && completed.Length >= count &&
                !completed.Any(e => snapshot.Header.Definition.Nodes[e.NodeId!].Type == NodeTypes.Output);
            if (WorkflowCheckpointShape.UsesLoopState(revision.Definition) &&
                !snapshot.Events.Any(evt => evt.NodeId is not null &&
                    revision.Definition.Nodes.TryGetValue(evt.NodeId, out var node) && node.Type == NodeTypes.Loop &&
                    evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped))
            {
                boundary = null;
                return ValueTask.FromResult(false);
            }
            if (WorkflowCheckpointShape.UsesLoopState(revision.Definition) &&
                boundary is { Observation.NextNodeId: null })
            {
                boundary = null;
                return ValueTask.FromResult(false);
            }
            var hash = WorkflowRunCheckpoint.Hash(native.Payload);
            var metadata = JsonSerializer.Serialize(new { native.CheckpointId, payloadHash = hash, runtimeVersion = WorkflowRunner.CheckpointRuntimeVersion });
            var events = new List<ExecutionEvent>
            {
                new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 1, clock.GetUtcNow(),
                    ExecutionEventKinds.CheckpointCaptured, Data: metadata)
            };
            if (suspended) events.Add(new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Events.Length + 2,
                clock.GetUtcNow(), ExecutionEventKinds.Suspended, Data: "Saved deterministic boundary; explicit host recovery required."));
            var checkpoint = new WorkflowRunCheckpoint("1.0", WorkflowRunner.CheckpointRuntimeVersion, snapshot.Header.RunId,
                snapshot.Header.RevisionId, snapshot.Header.ContentHash, snapshot.Events.Length + events.Count, native, hash)
            {
                VariableStateHash = native.VariableState?.ComputeHash()
            };
            if (forEachGraph)
            {
                var observed = boundary ?? throw new InvalidOperationException("The For Each checkpoint boundary was not captured by the host.");
                var state = observed.Observation.ForEach!;
                if (!string.Equals(observed.Checkpoint.CheckpointId, native.CheckpointId, StringComparison.Ordinal) ||
                    !string.Equals(observed.Checkpoint.Payload, native.Payload, StringComparison.Ordinal) ||
                    native.ForEach != state)
                    throw new InvalidOperationException("The For Each checkpoint boundary does not match the native checkpoint being committed.");
                var anchor = snapshot.Events.LastOrDefault(evt => evt.NodeId == state.ForEachNodeId &&
                    evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped)
                    ?? throw new InvalidOperationException("The For Each checkpoint boundary has no recorded iteration evidence.");
                var items = ForEachCheckpointCursor.CanonicalItems(state.ItemsJson);
                var forEachCursor = new ForEachCheckpointCursor(state.ForEachNodeId, state.PassIndex,
                    ForEachCheckpointCursor.HashItems(items), items, anchor.Sequence);
                checkpoint = checkpoint with
                {
                    ForEachFrame = WorkflowForEachCheckpointFrame.Create(revision, snapshot.Header.RunId, native.Payload,
                        forEachCursor, snapshot.Events.Concat(events).ToArray())
                };
            }
            else if (WorkflowCheckpointShape.UsesLoopState(revision.Definition))
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
