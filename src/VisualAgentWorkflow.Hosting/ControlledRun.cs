using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>One process-owned continuation and sequenced journal writer. The framework still executes the graph.</summary>
internal sealed class ControlledRun(IWorkflowRunStore journal, RunHeader header, SemaphoreSlim executionSlot,
    TimeProvider? timeProvider = null, WorkflowWaitOptions? waitOptions = null)
    : IWorkflowFrameExecutionGate, IWorkflowApprovalGate, IWorkflowWaitGate, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly WorkflowWaitOptions waits = waitOptions ?? WorkflowWaitOptions.Default;
    /// <summary>True while this run counts toward the host's waiting-run cap (its definition contains a Wait node).</summary>
    public bool HoldsWaitCapacity { get; } = WorkflowWaitOptions.ContainsWait(header.Definition);
    private Guid? activeWaitId;
    private Action<string, string>? settleActiveWait;
    private long cumulativeWaitMilliseconds;
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
    private (string NodeId, string? ScopeId)? skipBreakpointOnce;
    private string? waitingForScope;
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
            skipBreakpointOnce = waitingForNode is null ? null : (waitingForNode, waitingForScope);
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
            skipBreakpointOnce = waitingForNode is null ? null : (waitingForNode, waitingForScope);
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

    public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken) =>
        WaitForBoundaryCoreAsync(nextNodeId, null, evaluateBreakpoints: true, cancellationToken);

    public ValueTask WaitForBoundaryAsync(WorkflowBoundary boundary, CancellationToken cancellationToken) =>
        WaitForBoundaryCoreAsync(boundary.NextNodeId, boundary.ScopeId, evaluateBreakpoints: true, cancellationToken);

    /// <summary>
    /// Boundary wait. A caller that already passed this node's breakpoint boundary (a Wait reacquiring its slot after it
    /// settles) skips breakpoint evaluation so the node does not hit its own breakpoint twice.
    /// </summary>
    private async ValueTask WaitForBoundaryCoreAsync(string? nextNodeId, string? scopeId, bool evaluateBreakpoints, CancellationToken cancellationToken)
    {
        var boundaryEvaluated = !evaluateBreakpoints;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? resumeSignal = null;
            lock (gate)
            {
                RequireOwnedExecution();
                inFlight = false;
                waitingForNode = nextNodeId;
                waitingForScope = scopeId;
                if (pauseAfterSteppedNode)
                {
                    pauseAfterSteppedNode = false;
                    pauseRequested = true;
                }
                if (!boundaryEvaluated && nextNodeId is not null && breakpoints.Contains(nextNodeId) &&
                    skipBreakpointOnce != (nextNodeId, scopeId))
                {
                    Append(new(header.RunId, header.RevisionId, 0, clock.GetUtcNow(),
                        ExecutionEventKinds.BreakpointHit, nextNodeId,
                        Data: scopeId is null ? JsonSerializer.Serialize(new { nodeId = nextNodeId })
                            : JsonSerializer.Serialize(new { nodeId = nextNodeId, scopeId })));
                    RecordControl(ExecutionEventKinds.PauseRequested, nextNodeId);
                    pauseRequested = true;
                }
                if (!boundaryEvaluated && nextNodeId is not null && skipBreakpointOnce == (nextNodeId, scopeId)) skipBreakpointOnce = null;
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


    /// <summary>
    /// Decision 0081 Wait: hold this run for a bounded duration on the monotonic clock. The execution slot is released
    /// while waiting and reacquired before the node completes. Exactly one of wait.elapsed, wait.cancelled or
    /// wait.interrupted is recorded under the gate lock. The timer task decides the winner: a timer that completed
    /// before cancellation was observed settles as elapsed (a later cancellation then ends the run at the next
    /// boundary); otherwise stop/cancel settles as cancelled and a host shutdown settles as interrupted. Pause is
    /// acknowledged immediately and never stops the clock. Process loss leaves wait.requested unsettled, which
    /// reconciliation reports as unresolved, non-resumable work.
    /// </summary>
    public async ValueTask WaitAsync(WorkflowWaitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waitId = Guid.NewGuid();
        Task timer;
        long startTimestamp;
        var milliseconds = (long)request.Duration.TotalMilliseconds;
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate)
        {
            RequireOwnedExecution();
            cancellationToken.ThrowIfCancellationRequested();
            // The host re-derives the duration from the captured definition; the runner's value is only a cross-check.
            if (!header.Definition.Nodes.TryGetValue(request.NodeId, out var node) || node?.Type != NodeTypes.Wait ||
                node.Wait is not { } configured || configured.DurationMilliseconds != milliseconds ||
                request.Duration != TimeSpan.FromMilliseconds(configured.DurationMilliseconds))
                throw new InvalidOperationException("The wait request does not match the captured workflow.");
            if (activeWaitId is not null) throw new InvalidOperationException("A wait is already pending.");
            if (milliseconds < WorkflowWaitLimits.MinimumDurationMilliseconds || request.Duration > waits.MaximumWait)
                throw new InvalidOperationException("The wait exceeds the host per-wait maximum.");
            if (cumulativeWaitMilliseconds + milliseconds > waits.CumulativeBudget.TotalMilliseconds)
                throw new InvalidOperationException("The wait exceeds the host per-run wait budget.");
            // Create the timer before the request is recorded, so a recorded request always has a live timer.
            startTimestamp = clock.GetTimestamp();
            timer = Task.Delay(request.Duration, clock, timerCancellation.Token);
            try
            {
                // loopIteration is appended only inside a Loop or For Each body, so a top-level payload is unchanged.
                var bound = new
                {
                    perWaitMaximumMilliseconds = (long)waits.MaximumWait.TotalMilliseconds,
                    cumulativeBudgetMilliseconds = (long)waits.CumulativeBudget.TotalMilliseconds,
                    cumulativeRequestedMilliseconds = cumulativeWaitMilliseconds + milliseconds
                };
                // Display only. The wait is measured on the monotonic clock, never against this wall-clock value.
                var deadlineUtc = clock.GetUtcNow() + request.Duration;
                if (request.LoopIteration is { } iteration)
                    RecordWait(ExecutionEventKinds.WaitRequested, new
                    {
                        waitId, nodeId = request.NodeId, durationMilliseconds = milliseconds, deadlineUtc, bound, loopIteration = iteration
                    }, request);
                else
                    RecordWait(ExecutionEventKinds.WaitRequested, new
                    {
                        waitId, nodeId = request.NodeId, durationMilliseconds = milliseconds, deadlineUtc, bound
                    }, request);
            }
            catch
            {
                timerCancellation.Cancel();
                throw;
            }
            cumulativeWaitMilliseconds += milliseconds;
            activeWaitId = waitId;
            // Settles this wait once (kind, reason). Callers hold the gate lock. Never throws: a journal that cannot be
            // written leaves the request unsettled, which reconciliation then reports, and must not mask the caller.
            settleActiveWait = (kind, reason) =>
            {
                if (activeWaitId != waitId) return;
                activeWaitId = null;
                settleActiveWait = null;
                if (terminal) return;
                try
                {
                    var elapsedMilliseconds = (long)clock.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    if (request.LoopIteration is { } iteration)
                        RecordWait(kind, new
                        {
                            waitId, nodeId = request.NodeId, durationMilliseconds = milliseconds, elapsedMilliseconds, reason,
                            loopIteration = iteration
                        }, request);
                    else
                        RecordWait(kind, new
                        {
                            waitId, nodeId = request.NodeId, durationMilliseconds = milliseconds, elapsedMilliseconds, reason
                        }, request);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            };
            inFlight = false;
            waitingForNode = request.NodeId;
            ReleaseExecutionSlot();
            if (pauseRequested) AcknowledgePause(request.NodeId);
        }

        var elapsed = false;
        try
        {
            await timer;
            elapsed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                if (elapsed) settleActiveWait?.Invoke(ExecutionEventKinds.WaitElapsed, "The requested duration elapsed.");
                else if (cancellationRequested) settleActiveWait?.Invoke(ExecutionEventKinds.WaitCancelled, "The run was stopped or cancelled.");
                else settleActiveWait?.Invoke(ExecutionEventKinds.WaitInterrupted, "The host stopped while the wait was pending.");
            }
        }
        // Reacquire execution ownership before the node completes. Pause and stop are honoured here.
        await WaitForBoundaryCoreAsync(request.NodeId, waitingForScope, evaluateBreakpoints: false, cancellationToken);
        lock (gate) { if (skipBreakpointOnce is { } skipped && skipped.NodeId == request.NodeId) skipBreakpointOnce = null; }
    }

    private void RecordWait<T>(string kind, T data, WorkflowWaitRequest request) => Append(new(header.RunId, header.RevisionId, 0,
        clock.GetUtcNow(), kind, request.NodeId, request.InvocationId, Data: JsonSerializer.Serialize(data, JsonSerializerOptions.Web))
    { ScopeId = request.ScopeId });

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
            // A wait still pending when the owner goes away is settled now, so a live journal never keeps an unsettled request.
            settleActiveWait?.Invoke(ExecutionEventKinds.WaitInterrupted, "The run's owner stopped while the wait was pending.");
            ReleaseExecutionSlot();
            Cancellation.Dispose();
        }
    }
}
