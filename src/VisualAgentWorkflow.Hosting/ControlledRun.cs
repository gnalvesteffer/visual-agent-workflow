using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>One process-owned continuation and sequenced journal writer. The framework still executes the graph.</summary>
internal sealed class ControlledRun(FileRunJournal journal, RunHeader header, SemaphoreSlim executionSlot)
    : IWorkflowExecutionGate, IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource resumed = NewSignal();
    private bool pauseRequested;
    private bool paused;
    private bool inFlight;
    private bool holdsExecutionSlot;
    private bool terminal;
    private bool disposed;
    private long sequence;
    private string? waitingForNode = header.Definition.EntryNodeId;
    public CancellationTokenSource Cancellation { get; } = new();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask RecordAsync(ExecutionEvent evt)
    {
        lock (gate) Append(evt);
        return ValueTask.CompletedTask;
    }

    private void Append(ExecutionEvent evt)
    {
        journal.Append(evt with { Sequence = sequence + 1 });
        sequence++;
        if (evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted)
            terminal = true;
    }

    private void RecordControl(string kind, string? nodeId = null) => Append(new(header.RunId, header.RevisionId, 0,
        DateTimeOffset.UtcNow, kind, NodeId: nodeId,
        Data: JsonSerializer.Serialize(new { actor = "local-developer", boundary = nodeId ?? "run-boundary" })));

    public void RequestPause()
    {
        lock (gate)
        {
            RequireOwnedExecution();
            if (pauseRequested) return;
            RecordControl(ExecutionEventKinds.PauseRequested);
            pauseRequested = true;
            if (!inFlight) AcknowledgePause(waitingForNode);
        }
    }

    public void Resume()
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            RequireOwnedExecution();
            if (!pauseRequested) return;
            RecordControl(ExecutionEventKinds.Resumed);
            pauseRequested = false; paused = false;
            signal = resumed;
            resumed = NewSignal();
        }
        signal.TrySetResult();
    }

    public void Cancel()
    {
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* Terminal cleanup won the race. */ }
    }

    public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? resumeSignal = null;
            lock (gate)
            {
                RequireOwnedExecution();
                inFlight = false;
                waitingForNode = nextNodeId;
                if (pauseRequested)
                {
                    AcknowledgePause(nextNodeId);
                    resumeSignal = resumed.Task;
                }
                else if (holdsExecutionSlot)
                {
                    inFlight = true;
                    return;
                }
            }
            if (resumeSignal is not null)
            {
                await resumeSignal.WaitAsync(cancellationToken);
                continue;
            }
            // A pause may arrive while waiting for the shared slot. Recheck under the
            // same admission lock before starting work, releasing the slot if paused.
            await executionSlot.WaitAsync(cancellationToken);
            lock (gate) holdsExecutionSlot = true;
        }
    }

    private void AcknowledgePause(string? nextNodeId)
    {
        if (!paused)
        {
            RecordControl(ExecutionEventKinds.Paused, nextNodeId);
            paused = true;
        }
        ReleaseExecutionSlot();
    }

    private void ReleaseExecutionSlot()
    {
        if (!holdsExecutionSlot) return;
        holdsExecutionSlot = false;
        executionSlot.Release();
    }

    private void RequireOwnedExecution()
    {
        if (terminal || disposed) throw new WorkflowRunControlException();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ReleaseExecutionSlot();
            Cancellation.Dispose();
        }
    }
}
