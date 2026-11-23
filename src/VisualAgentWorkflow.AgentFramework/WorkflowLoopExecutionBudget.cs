using System.Collections.Concurrent;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

internal sealed class WorkflowLoopExecutionBudget
{
    private readonly int maximumEvidenceEvents;
    private readonly ConcurrentDictionary<string, byte> loopScopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loopScopedEvidenceScopes = new(StringComparer.Ordinal);
    private int evidenceEvents;

    public WorkflowLoopExecutionBudget(int? maximumEvidenceEvents = null)
    {
        this.maximumEvidenceEvents = maximumEvidenceEvents ?? WorkflowLoopLimits.MaximumScopedEvidenceEventsPerRun;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.maximumEvidenceEvents);
    }

    public int MaximumEvidenceEvents => maximumEvidenceEvents;

    public void RegisterLoopScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        loopScopes.TryAdd(scopeId, 0);
    }

    public bool TryReserveEvidenceEvent(string? scopeId)
    {
        if (!IsLoopScoped(scopeId)) return true;
        return Interlocked.Increment(ref evidenceEvents) <= maximumEvidenceEvents ||
            ReleaseExceeded(ref evidenceEvents);
    }

    private bool IsLoopScoped(string? scopeId)
    {
        if (string.IsNullOrEmpty(scopeId)) return false;
        if (loopScopedEvidenceScopes.ContainsKey(scopeId)) return true;

        foreach (var loopScope in loopScopes.Keys)
        {
            if (scopeId == loopScope || scopeId.StartsWith(loopScope + "/", StringComparison.Ordinal))
            {
                loopScopedEvidenceScopes.TryAdd(scopeId, 0);
                return true;
            }
        }
        return false;
    }

    private static bool ReleaseExceeded(ref int value)
    {
        Interlocked.Decrement(ref value);
        return false;
    }
}

internal sealed class WorkflowLoopPassBudget(int? maximumBodyPasses = null)
{
    private readonly int maximumBodyPasses = maximumBodyPasses ?? WorkflowLoopLimits.MaximumTotalBodyPassesPerRun;
    private int bodyPasses;

    public bool TryReserveBodyPass() => Interlocked.Increment(ref bodyPasses) <= maximumBodyPasses ||
        ReleaseExceeded(ref bodyPasses);

    private static bool ReleaseExceeded(ref int value)
    {
        Interlocked.Decrement(ref value);
        return false;
    }
}
