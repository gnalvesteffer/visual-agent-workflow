using VisualAgentWorkflow.Core;
using System.Text.Json;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Tracks node, Loop iteration, and Sequence output work that has started but has not reached accepted terminal evidence.
/// This is runner-owned accounting, not persisted workflow state.
/// </summary>
internal sealed class ExecutionActivationLedger
{
    private readonly object gate = new();
    private readonly HashSet<ActivationKey> active = [];
    private readonly Dictionary<SequenceActivationKey, SequenceActivationWork> activeSequenceOutputs = [];
    private readonly Dictionary<LoopIterationKey, LoopIterationWork> activeLoopIterations = [];

    public void Validate(ExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        lock (gate)
        {
            if (executionEvent.NodeId is null && executionEvent.Kind is
                (ExecutionEventKinds.Completed or ExecutionEventKinds.Terminated) &&
                (active.Count != 0 || activeSequenceOutputs.Count != 0 || activeLoopIterations.Count != 0))
                throw new InvalidOperationException(
                    $"Workflow reached a successful terminal boundary with active work: {DescribeActiveWork()}.");

            if (TryGetLoopIteration(executionEvent, out var loopKey, out var loopWork))
            {
                if (executionEvent.Kind == ExecutionEventKinds.LoopIterationStarted)
                {
                    if (activeLoopIterations.ContainsKey(loopKey))
                        throw new InvalidOperationException("A Loop iteration was started more than once before settlement.");
                }
                else if (!activeLoopIterations.TryGetValue(loopKey, out var activeLoopWork) || activeLoopWork != loopWork)
                    throw new InvalidOperationException("Loop iteration settlement does not match an accepted active iteration.");
                return;
            }

            if (!SequenceEvidence.IsLifecycleEvent(executionEvent.Kind) ||
                executionEvent.Kind == ExecutionEventKinds.SequenceBreakAccepted ||
                executionEvent.Kind is ExecutionEventKinds.SequenceStarted or ExecutionEventKinds.SequenceCompleted or
                    ExecutionEventKinds.SequenceBreakCompleted or ExecutionEventKinds.SequenceFailed or
                    ExecutionEventKinds.SequenceCancelled or ExecutionEventKinds.SequenceOutputSkipped)
                return;

            if (!TryGetSequenceWork(executionEvent, out var key, out var work))
                throw new InvalidOperationException("Sequence output evidence is malformed or does not match its event identity.");

            switch (executionEvent.Kind)
            {
                case ExecutionEventKinds.SequenceOutputDispatched:
                    if (activeSequenceOutputs.ContainsKey(key) ||
                        activeSequenceOutputs.Values.Any(activeWork => activeWork.SequenceInvocationId == work.SequenceInvocationId &&
                            activeWork.ScopeId == work.ScopeId && activeWork.OutputId == work.OutputId))
                        throw new InvalidOperationException("Sequence output work was dispatched more than once.");
                    break;
                case ExecutionEventKinds.SequenceOutputCompleted:
                case ExecutionEventKinds.SequenceOutputFailed:
                case ExecutionEventKinds.SequenceOutputCancelled:
                    if (!activeSequenceOutputs.TryGetValue(key, out var activeWork) || activeWork != work)
                        throw new InvalidOperationException("Sequence output settlement does not match accepted active work.");
                    break;
            }
        }
    }

    public void Observe(ExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        if (TryGetLoopIteration(executionEvent, out var loopKey, out var loopWork))
        {
            lock (gate)
            {
                if (executionEvent.Kind == ExecutionEventKinds.LoopIterationStarted)
                    activeLoopIterations.Add(loopKey, loopWork);
                else
                    activeLoopIterations.Remove(loopKey);
            }
        }

        if (executionEvent.NodeId is null || executionEvent.InvocationId is not { } invocationId)
            return;

        var key = new ActivationKey(executionEvent.ScopeId, executionEvent.NodeId, invocationId);
        lock (gate)
        {
            switch (executionEvent.Kind)
            {
                case ExecutionEventKinds.NodeStarted:
                    active.Add(key);
                    break;
                case ExecutionEventKinds.NodeCompleted:
                case ExecutionEventKinds.NodeFailed:
                case ExecutionEventKinds.NodeCancelled:
                case ExecutionEventKinds.SequenceBreakCompleted:
                    // The proof scheduler leaves its generic Sequence activation
                    // open until Break settlement; production Sequence dispatch may
                    // already have completed that generic node before a descendant
                    // reports Break, in which case this is an intentional no-op.
                    active.Remove(key);
                    break;
            }

            if (TryGetSequenceWork(executionEvent, out var sequenceKey, out var sequenceWork))
            {
                if (executionEvent.Kind == ExecutionEventKinds.SequenceOutputDispatched)
                    activeSequenceOutputs.Add(sequenceKey, sequenceWork);
                else if (executionEvent.Kind is ExecutionEventKinds.SequenceOutputCompleted or
                         ExecutionEventKinds.SequenceOutputFailed or ExecutionEventKinds.SequenceOutputCancelled)
                    activeSequenceOutputs.Remove(sequenceKey);
            }
        }
    }

    public IReadOnlyList<ExecutionActivation> Snapshot()
    {
        lock (gate)
            return active.OrderBy(item => item.ScopeId, StringComparer.Ordinal)
                .ThenBy(item => item.NodeId, StringComparer.Ordinal)
                .ThenBy(item => item.InvocationId)
                .Select(item => new ExecutionActivation(item.ScopeId, item.NodeId, item.InvocationId))
                .ToArray();
    }

    public void ThrowIfActive()
    {
        var unresolved = Snapshot();
        SequenceActivationWork[] unresolvedSequence;
        lock (gate) unresolvedSequence = activeSequenceOutputs.Values.ToArray();
        LoopIterationWork[] unresolvedLoops;
        lock (gate) unresolvedLoops = activeLoopIterations.Values.ToArray();
        if (unresolved.Count == 0 && unresolvedSequence.Length == 0 && unresolvedLoops.Length == 0) return;

        var details = unresolved.Select(item =>
                $"{(item.ScopeId is null ? "root" : item.ScopeId)}/{item.NodeId}/{item.InvocationId}")
            .Concat(unresolvedSequence.Select(item =>
                $"{(item.ScopeId is null ? "root" : item.ScopeId)}/{item.SequenceNodeId}/{item.SequenceInvocationId}/{item.OutputId}/{item.ActivationId}"))
            .Concat(unresolvedLoops.Select(item =>
                $"{(item.ScopeId is null ? "root" : item.ScopeId)}/{item.LoopNodeId}/{item.Iteration}"));
        throw new InvalidOperationException($"Workflow reached its terminal boundary with active work: {string.Join(", ", details)}.");
    }

    private static bool TryGetLoopIteration(ExecutionEvent executionEvent, out LoopIterationKey key,
        out LoopIterationWork work)
    {
        key = default;
        work = default!;
        if (executionEvent.Kind is not (ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.LoopIterationCompleted)) return false;
        if (string.IsNullOrWhiteSpace(executionEvent.NodeId) || executionEvent.InvocationId is null)
            throw new InvalidOperationException("Loop iteration evidence is missing its node or invocation identity.");

        int? iteration = null;
        if (!string.IsNullOrWhiteSpace(executionEvent.Data))
        {
            try
            {
                using var payload = JsonDocument.Parse(executionEvent.Data);
                if (payload.RootElement.ValueKind == JsonValueKind.Object &&
                    payload.RootElement.TryGetProperty("iteration", out var value) &&
                    value.TryGetInt32(out var parsed) && parsed > 0)
                    iteration = parsed;
            }
            catch (JsonException) { }
        }

        if (executionEvent.ScopeId is null && iteration is null)
            throw new InvalidOperationException("An unscoped Loop iteration requires an explicit iteration number.");
        key = new(executionEvent.NodeId, executionEvent.ScopeId, executionEvent.ScopeId is null ? iteration : null);
        work = new(executionEvent.NodeId, executionEvent.ScopeId, iteration);
        return true;
    }

    private string DescribeActiveWork() => string.Join(", ",
        active.Select(item => $"node:{(item.ScopeId is null ? "root" : item.ScopeId)}/{item.NodeId}/{item.InvocationId}")
            .Concat(activeLoopIterations.Keys.Select(item =>
                $"loop:{(item.ScopeId ?? "root")}/{item.NodeId}/{item.Iteration?.ToString() ?? "scope"}"))
            .Concat(activeSequenceOutputs.Values.Select(item =>
                $"sequence:{(item.ScopeId ?? "root")}/{item.SequenceNodeId}/{item.SequenceInvocationId}/{item.OutputId}/{item.ActivationId}")));

    private static bool TryGetSequenceWork(ExecutionEvent executionEvent, out SequenceActivationKey key,
        out SequenceActivationWork work)
    {
        key = default;
        work = default!;
        if (executionEvent.Kind is not (ExecutionEventKinds.SequenceOutputDispatched or
            ExecutionEventKinds.SequenceOutputCompleted or ExecutionEventKinds.SequenceOutputFailed or
            ExecutionEventKinds.SequenceOutputCancelled)) return false;
        if (!SequenceEvidence.TryRead(executionEvent, out var evidence) || evidence is null ||
            evidence.OutputId is null || evidence.Ordinal is not { } ordinal || ordinal < 0 ||
            evidence.ActivationId is not { } activationId || activationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(evidence.ConnectionId) ||
            executionEvent.NodeId != evidence.SequenceNodeId ||
            executionEvent.InvocationId != evidence.SequenceInvocationId ||
            executionEvent.ConnectionId != evidence.ConnectionId)
            return false;

        key = new(executionEvent.ScopeId, evidence.SequenceInvocationId, activationId);
        work = new(executionEvent.ScopeId, evidence.SequenceInvocationId, evidence.SequenceNodeId,
            evidence.OutputId, ordinal, activationId, evidence.ConnectionId);
        return true;
    }

    private readonly record struct ActivationKey(string? ScopeId, string NodeId, Guid InvocationId);
    private readonly record struct LoopIterationKey(string NodeId, string? ScopeId, int? Iteration);
    private sealed record LoopIterationWork(string LoopNodeId, string? ScopeId, int? Iteration);
    private readonly record struct SequenceActivationKey(string? ScopeId, Guid SequenceInvocationId, Guid ActivationId);
    private sealed record SequenceActivationWork(string? ScopeId, Guid SequenceInvocationId,
        string SequenceNodeId, string OutputId, int Ordinal, Guid ActivationId, string ConnectionId);
}

internal sealed record ExecutionActivation(string? ScopeId, string NodeId, Guid InvocationId);
