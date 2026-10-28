using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Tracks node work that has started but has not reached a terminal node event.
/// This is runner-owned accounting, not persisted workflow state.
/// </summary>
internal sealed class ExecutionActivationLedger
{
    private readonly object gate = new();
    private readonly HashSet<ActivationKey> active = [];

    public void Observe(ExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
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
                    active.Remove(key);
                    break;
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
        if (unresolved.Count == 0) return;

        var details = string.Join(", ", unresolved.Select(item =>
            $"{(item.ScopeId is null ? "root" : item.ScopeId)}/{item.NodeId}/{item.InvocationId}"));
        throw new InvalidOperationException($"Workflow reached its terminal boundary with active node work: {details}.");
    }

    private readonly record struct ActivationKey(string? ScopeId, string NodeId, Guid InvocationId);
}

internal sealed record ExecutionActivation(string? ScopeId, string NodeId, Guid InvocationId);
