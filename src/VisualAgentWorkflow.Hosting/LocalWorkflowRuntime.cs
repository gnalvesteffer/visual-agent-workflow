using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Bounded, process-owned execution. Paused continuations release the shared execution slot.</summary>
public sealed class LocalWorkflowRuntime(FileRunJournal journal, ILogger<LocalWorkflowRuntime> logger, INodeToolBroker? tools = null, IAgentNodeExecutor? agents = null)
    : BackgroundService, IWorkflowClient, IWorkflowRunControl
{
    private const int MaximumOwnedRuns = 32;
    private sealed record Request(Guid RunId, WorkflowRevision Revision, string Input, ControlledRun Control);
    private readonly Channel<Request> queue = Channel.CreateBounded<Request>(MaximumOwnedRuns);
    private readonly ConcurrentDictionary<Guid, ControlledRun> ownedRuns = new();
    private readonly SemaphoreSlim executionSlot = new(1, 1);
    private readonly object admission = new();
    private bool accepting;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (admission) journal.MarkUnfinishedAsInterrupted();
        await base.StartAsync(cancellationToken);
        lock (admission) accepting = true;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (admission) accepting = false;
        return base.StopAsync(cancellationToken);
    }

    public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) =>
        StartRevisionAsync(WorkflowRevision.Create(definition, "local-developer"), input);

    public Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input) =>
        StartRevisionPausedAsync(WorkflowRevision.Create(definition, "local-developer"), input);

    public Task<RunSnapshot> StartRevisionAsync(WorkflowRevision revision, string input) => StartRevision(revision, input, false);
    public Task<RunSnapshot> StartRevisionPausedAsync(WorkflowRevision revision, string input) => StartRevision(revision, input, true);

    private Task<RunSnapshot> StartRevision(WorkflowRevision revision, string input, bool startPaused)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        lock (admission)
        {
            var snapshot = journal.Create(revision, input);
            if (accepting && ownedRuns.Count < MaximumOwnedRuns)
            {
                var control = new ControlledRun(journal, snapshot.Header, executionSlot);
                if (startPaused) control.RequestPause();
                ownedRuns[snapshot.Header.RunId] = control;
                if (queue.Writer.TryWrite(new(snapshot.Header.RunId, revision, input, control)))
                    return Task.FromResult(journal.Get(snapshot.Header.RunId)!);
                ownedRuns.TryRemove(snapshot.Header.RunId, out _);
                control.Dispose();
            }
            var latest = journal.Get(snapshot.Header.RunId)!;
            journal.Append(new(snapshot.Header.RunId, revision.RevisionId, latest.Events.Length + 1, DateTimeOffset.UtcNow,
                ExecutionEventKinds.Failed, Data: "The local execution queue is full or stopping."));
            return Task.FromResult(journal.Get(snapshot.Header.RunId)!);
        }
    }

    public Task<RunSnapshot?> GetRunAsync(Guid runId) => Task.FromResult(journal.Get(runId));
    public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => Task.FromResult(journal.List());

    public Task PauseRunAsync(Guid runId) { RequireOwnedRun(runId).RequestPause(); return Task.CompletedTask; }
    public Task ResumeRunAsync(Guid runId) { RequireOwnedRun(runId).Resume(); return Task.CompletedTask; }
    public Task CancelRunAsync(Guid runId)
    {
        if (ownedRuns.TryGetValue(runId, out var run)) run.Cancel();
        return Task.CompletedTask;
    }

    private ControlledRun RequireOwnedRun(Guid id)
    {
        if (ownedRuns.TryGetValue(id, out var control)) return control;
        if (journal.Get(id) is null) throw new KeyNotFoundException("Unknown run.");
        throw new WorkflowRunControlException();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var continuations = new List<Task>();
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                continuations.RemoveAll(task => task.IsCompleted);
                // Each context can wait independently, but the gate admits only one executing
                // workflow at a time. A pause releases that slot without rebuilding the graph.
                continuations.Add(Task.Run(() => ExecuteRequest(request, stoppingToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (admission) accepting = false;
            queue.Writer.TryComplete();
            await Task.WhenAll(continuations);
            while (queue.Reader.TryRead(out var pending))
            {
                ownedRuns.TryRemove(pending.RunId, out _);
                pending.Control.Dispose();
            }
            lock (admission) journal.MarkUnfinishedAsInterrupted();
        }
    }

    private async Task ExecuteRequest(Request request, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.Control.Cancellation.Token);
        try
        {
            await new PipelineRunner(tools, agents).ExecuteControlledAsync(request.RunId, request.Revision, request.Input,
                request.Control.RecordAsync, request.Control, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError("Run {RunId} failed with {ExceptionType}.", request.RunId, error.GetType().Name);
        }
        finally
        {
            ownedRuns.TryRemove(request.RunId, out _);
            request.Control.Dispose();
        }
    }
}
