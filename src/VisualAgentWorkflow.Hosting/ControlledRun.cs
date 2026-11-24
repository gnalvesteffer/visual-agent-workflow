using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>One process-owned continuation and sequenced journal writer. The framework still executes the graph.</summary>
internal sealed class ControlledRun(IWorkflowRunStore journal, RunHeader header, SemaphoreSlim executionSlot,
    TimeProvider? timeProvider = null)
    : IWorkflowExecutionGate, IWorkflowApprovalGate, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private TaskCompletionSource resumed = NewSignal();
    private bool pauseRequested;
    private bool paused;
    private bool inFlight;
    private bool holdsExecutionSlot;
    private bool terminal;
    private bool disposed;
    private bool cancellationRequested;
    private readonly HashSet<string> breakpoints = new(StringComparer.Ordinal);
    private string? skipBreakpointOnce;
    private bool stepNextNode;
    private bool pauseAfterSteppedNode;
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
        clock.GetUtcNow(), kind, NodeId: nodeId,
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
            skipBreakpointOnce = waitingForNode;
            pauseRequested = false; paused = false;
            signal = resumed;
            resumed = NewSignal();
        }
        signal.TrySetResult();
    }

    public void SetBreakpoints(IReadOnlyCollection<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count > 256 || nodeIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
                !WorkflowDebugging.SupportsBreakpoint(header.Definition, id)))
            throw new ArgumentException("Breakpoints must reference executable workflow node boundaries (maximum 256).", nameof(nodeIds));
        lock (gate)
        {
            RequireOwnedExecution();
            if (breakpoints.SetEquals(nodeIds)) return;
            breakpoints.Clear();
            breakpoints.UnionWith(nodeIds);
            Append(new(header.RunId, header.RevisionId, 0, clock.GetUtcNow(),
                ExecutionEventKinds.BreakpointsChanged, Data: JsonSerializer.Serialize(new { nodeIds = breakpoints.Order(StringComparer.Ordinal).ToArray() })));
        }
    }

    public void Step()
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            RequireOwnedExecution();
            if (!paused || string.IsNullOrWhiteSpace(waitingForNode))
                throw new InvalidOperationException("Step requires a paused run waiting before a workflow node.");
            if (!WorkflowDebugging.SupportsNodeStepping(header.Definition))
                throw new InvalidOperationException("Node stepping is currently supported for sequential workflows without branching, loops, parallel regions, or nested workflow calls.");
            RecordControl(ExecutionEventKinds.Resumed, waitingForNode);
            skipBreakpointOnce = waitingForNode;
            pauseRequested = false;
            paused = false;
            stepNextNode = true;
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
        var boundaryEvaluated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? resumeSignal = null;
            lock (gate)
            {
                RequireOwnedExecution();
                inFlight = false;
                waitingForNode = nextNodeId;
                if (pauseAfterSteppedNode)
                {
                    pauseAfterSteppedNode = false;
                    pauseRequested = true;
                }
                if (!boundaryEvaluated && nextNodeId is not null && breakpoints.Contains(nextNodeId) && skipBreakpointOnce != nextNodeId)
                {
                    Append(new(header.RunId, header.RevisionId, 0, clock.GetUtcNow(),
                        ExecutionEventKinds.BreakpointHit, nextNodeId,
                        Data: JsonSerializer.Serialize(new { nodeId = nextNodeId })));
                    RecordControl(ExecutionEventKinds.PauseRequested, nextNodeId);
                    pauseRequested = true;
                }
                if (!boundaryEvaluated && skipBreakpointOnce == nextNodeId) skipBreakpointOnce = null;
                boundaryEvaluated = true;
                if (pauseRequested)
                {
                    AcknowledgePause(nextNodeId);
                    resumeSignal = resumed.Task;
                }
                else if (holdsExecutionSlot)
                {
                    if (stepNextNode)
                    {
                        stepNextNode = false;
                        pauseAfterSteppedNode = true;
                    }
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
            request = new(Guid.NewGuid(), action, action.ComputeHash(), prompt, clock.GetUtcNow());
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
        clock.GetUtcNow(), kind, nodeId, invocationId, Data: JsonSerializer.Serialize(data, JsonSerializerOptions.Web)) { ScopeId = scopeId });

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
