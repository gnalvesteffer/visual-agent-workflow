using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ExecutionActivationLedgerTests
{
    [Fact]
    public void Terminal_node_event_removes_only_its_activation()
    {
        var ledger = new ExecutionActivationLedger();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        ledger.Observe(Event(ExecutionEventKinds.NodeStarted, "first", first));
        ledger.Observe(Event(ExecutionEventKinds.NodeStarted, "second", second));
        ledger.Observe(Event(ExecutionEventKinds.NodeCompleted, "first", first));

        var active = Assert.Single(ledger.Snapshot());
        Assert.Equal("second", active.NodeId);
        Assert.Equal(second, active.InvocationId);
        Assert.Throws<InvalidOperationException>(() => ledger.ThrowIfActive());

        ledger.Observe(Event(ExecutionEventKinds.NodeFailed, "second", second));
        ledger.ThrowIfActive();
    }

    [Fact]
    public void Scope_is_part_of_activation_identity()
    {
        var ledger = new ExecutionActivationLedger();
        var invocation = Guid.NewGuid();

        ledger.Observe(Event(ExecutionEventKinds.NodeStarted, "agent", invocation, "child"));
        ledger.Observe(Event(ExecutionEventKinds.NodeCompleted, "agent", invocation, null));

        Assert.Single(ledger.Snapshot());
    }

    private static ExecutionEvent Event(string kind, string nodeId, Guid invocationId, string? scopeId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, kind, nodeId, invocationId) { ScopeId = scopeId };
}
