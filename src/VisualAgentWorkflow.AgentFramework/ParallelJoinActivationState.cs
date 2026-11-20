namespace VisualAgentWorkflow.AgentFramework;

internal readonly record struct ParallelJoinActivationKey(Guid? FrameId, Guid? ForkActivationId);

/// <summary>Serializes and fail-closes arrivals for one activation of a parallel Join.</summary>
internal sealed class ParallelJoinActivationState(Guid? frameId, Guid? forkActivationId)
{
    private readonly object gate = new();
    private readonly SortedDictionary<string, string> inputs = new(StringComparer.Ordinal);
    private Guid? invocationId;
    private bool failed;

    public ParallelJoinActivationKey Key { get; } = new(frameId, forkActivationId);
    public SemaphoreSlim Execution { get; } = new(1, 1);

    public int InputCount
    {
        get { lock (gate) return inputs.Count; }
    }

    public void AddInput(string branchId, string value)
    {
        lock (gate)
        {
            ThrowIfFailedCore();
            if (inputs.TryAdd(branchId, value)) return;

            failed = true;
            throw new InvalidOperationException("The parallel join received a duplicate branch result.");
        }
    }

    public SortedDictionary<string, string> InputSnapshot()
    {
        lock (gate) return new(inputs, StringComparer.Ordinal);
    }

    public Guid GetOrCreateInvocationId()
    {
        lock (gate) return invocationId ??= Guid.NewGuid();
    }

    public void MarkFailed()
    {
        lock (gate) failed = true;
    }

    public void ThrowIfFailed()
    {
        lock (gate) ThrowIfFailedCore();
    }

    private void ThrowIfFailedCore()
    {
        if (failed) throw new InvalidOperationException("The parallel join activation has already failed.");
    }
}
