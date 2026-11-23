using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Serializes root execution-event delivery and advances accepted sequence and
/// activation state only after the owning recorder callback returns.
/// Lifecycle, uncertainty, and budget policy belong to the callback owner.
/// </summary>
internal sealed class SequencedExecutionEventWriter : IDisposable
{
    private readonly Guid runId;
    private readonly Guid revisionId;
    private readonly Func<ExecutionEvent, ValueTask> accept;
    private readonly ExecutionActivationLedger activationLedger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long sequence;

    public SequencedExecutionEventWriter(Guid runId, Guid revisionId,
        Func<ExecutionEvent, ValueTask> accept, ExecutionActivationLedger activationLedger)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (revisionId == Guid.Empty) throw new ArgumentException("A revision ID is required.", nameof(revisionId));
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(activationLedger);
        this.runId = runId;
        this.revisionId = revisionId;
        this.accept = accept;
        this.activationLedger = activationLedger;
    }

    public async ValueTask RecordAsync(string kind, string? nodeId = null, Guid? invocationId = null,
        string? scopeId = null, string? data = null, string? connectionId = null)
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var nextSequence = sequence + 1;
            var executionEvent = new ExecutionEvent(runId, revisionId, nextSequence, DateTimeOffset.UtcNow,
                kind, nodeId, invocationId, connectionId, data) { ScopeId = scopeId };
            activationLedger.Validate(executionEvent);
            await accept(executionEvent);
            activationLedger.Observe(executionEvent);
            sequence = nextSequence;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
