using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>One process-owned continuation and sequenced journal writer. The framework still executes the graph.</summary>
internal sealed class ControlledRun(IWorkflowRunStore journal, RunHeader header, SemaphoreSlim executionSlot)
    : IWorkflowExecutionGate, IWorkflowApprovalGate, IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource resumed = NewSignal();
    private bool pauseRequested;
    private bool paused;
    private bool inFlight;
    private bool holdsExecutionSlot;
    private bool terminal;
    private bool disposed;
    private bool cancellationRequested;
    private long sequence;
    private WorkflowApprovalRequest? pendingApproval;
    private TaskCompletionSource<bool>? approvalSignal;
    private WorkflowApprovalDecision? approvalDecision;
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
        if (evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Terminated or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted)
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
        lock (gate)
        {
            if (disposed) return;
            cancellationRequested = true;
        }
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

    public async ValueTask<WorkflowApprovalPermit> WaitForApprovalAsync(WorkflowApprovalAction action, string prompt, CancellationToken cancellationToken)
    {
        WorkflowApprovalRequest request;
        Task<bool> decision;
        lock (gate)
        {
            RequireOwnedExecution();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAction(action, prompt);
            if (pendingApproval is not null) throw new InvalidOperationException("An approval is already pending.");
            request = new(Guid.NewGuid(), action, action.ComputeHash(), prompt, DateTimeOffset.UtcNow);
            RecordApproval(ExecutionEventKinds.ApprovalRequested, request, action.ApprovalNodeId, action.ApprovalInvocationId, action.ScopeId);
            pendingApproval = request;
            approvalSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            approvalDecision = null;
            decision = approvalSignal.Task;
            inFlight = false;
            waitingForNode = action.ApprovalNodeId;
            ReleaseExecutionSlot();
            if (pauseRequested) AcknowledgePause(action.ApprovalNodeId);
        }
        bool approved;
        try { approved = await decision.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                if (approvalDecision is null)
                    RecordApproval(ExecutionEventKinds.ApprovalCancelled, new { request.RequestId, request.ActionHash, reason = "Run cancelled or owner stopped." },
                        action.ApprovalNodeId, action.ApprovalInvocationId, action.ScopeId);
            }
            throw;
        }
        finally
        {
            lock (gate)
            {
                pendingApproval = null;
                approvalSignal = null;
            }
        }
        if (!approved) throw new WorkflowApprovalRejectedException();
        // Approval and pause are independent. Reacquire execution ownership before completing the node.
        await WaitForBoundaryAsync(action.TargetNodeId, cancellationToken);
        return new(request.RequestId, request.ActionHash, action.TargetInvocationId);
    }

    public void DecideApproval(Guid requestId, string actionHash, bool approve, string actor)
    {
        TaskCompletionSource<bool> signal;
        lock (gate)
        {
            RequireOwnedExecution();
            if (cancellationRequested || Cancellation.IsCancellationRequested) throw new WorkflowApprovalConflictException();
            var request = pendingApproval;
            if (request is null || request.RequestId != requestId || request.ActionHash != actionHash)
                throw new WorkflowApprovalConflictException();
            if (approvalDecision is not null)
            {
                if (approvalDecision.Approved != approve) throw new WorkflowApprovalConflictException();
                return;
            }
            var decision = new WorkflowApprovalDecision(requestId, actionHash, approve, actor);
            RecordApproval(approve ? ExecutionEventKinds.ApprovalApproved : ExecutionEventKinds.ApprovalRejected, decision,
                request.Action.ApprovalNodeId, request.Action.ApprovalInvocationId, request.Action.ScopeId);
            approvalDecision = decision;
            signal = approvalSignal!;
        }
        signal.TrySetResult(approve);
    }

    private void ValidateAction(WorkflowApprovalAction action, string prompt)
    {
        if (!WorkflowApprovalEvidence.MatchesDefinition(header.Definition, header.RunId, header.RevisionId,
            header.ContentHash, action, prompt))
            throw new InvalidOperationException("Approval action does not match the captured workflow.");
    }

    private void RecordApproval<T>(string kind, T data, string nodeId, Guid invocationId, string? scopeId = null) => Append(new(header.RunId, header.RevisionId, 0,
        DateTimeOffset.UtcNow, kind, nodeId, invocationId, Data: JsonSerializer.Serialize(data, JsonSerializerOptions.Web)) { ScopeId = scopeId });

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
