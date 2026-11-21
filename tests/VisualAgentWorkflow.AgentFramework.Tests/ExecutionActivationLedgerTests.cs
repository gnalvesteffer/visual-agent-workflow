using System.Text.Json;
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

    [Fact]
    public void Sequence_break_completion_settles_its_node_activation()
    {
        var ledger = new ExecutionActivationLedger();
        var invocation = Guid.NewGuid();
        var started = Event(ExecutionEventKinds.NodeStarted, "sequence", invocation, "array-loop/0");
        var breakCompleted = Event(ExecutionEventKinds.SequenceBreakCompleted, "sequence", invocation, "array-loop/0");

        ledger.Validate(started);
        ledger.Observe(started);
        var unrelatedBreak = Event(ExecutionEventKinds.SequenceBreakCompleted, "sequence", Guid.NewGuid(), "array-loop/0");
        ledger.Validate(unrelatedBreak);
        ledger.Observe(unrelatedBreak);
        Assert.Single(ledger.Snapshot());
        ledger.Validate(breakCompleted);
        ledger.Observe(breakCompleted);

        ledger.ThrowIfActive();
        ledger.Validate(RootTerminal(ExecutionEventKinds.Completed));

        var alreadyCompletedSequence = new ExecutionActivationLedger();
        var completedBreak = Event(ExecutionEventKinds.SequenceBreakCompleted, "sequence", invocation, "array-loop/0");
        alreadyCompletedSequence.Validate(completedBreak);
        alreadyCompletedSequence.Observe(completedBreak);
        alreadyCompletedSequence.ThrowIfActive();
    }

    [Fact]
    public void Accepted_sequence_dispatches_remain_active_until_matching_output_settlements()
    {
        var ledger = new ExecutionActivationLedger();
        var first = SequenceEvent(ExecutionEventKinds.SequenceOutputDispatched, "scope-a", "first", 0);
        var second = SequenceEvent(ExecutionEventKinds.SequenceOutputDispatched, "scope-a", "second", 1);

        ledger.Validate(first);
        ledger.Observe(first);
        ledger.Validate(second);
        ledger.Observe(second);
        Assert.Throws<InvalidOperationException>(() => ledger.Validate(RootTerminal(ExecutionEventKinds.Completed)));
        ledger.Validate(RootTerminal(ExecutionEventKinds.Failed));
        ledger.Validate(RootTerminal(ExecutionEventKinds.Cancelled));
        ledger.Validate(RootTerminal(ExecutionEventKinds.OutcomeUnknown));

        var wrongSettlement = SequenceEvent(ExecutionEventKinds.SequenceOutputCompleted, "scope-a", "first", 0,
            first.InvocationId, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => ledger.Validate(wrongSettlement));

        var firstCompleted = SequenceEvent(ExecutionEventKinds.SequenceOutputCompleted, "scope-a", "first", 0,
            first.InvocationId, ReadEvidence(first).ActivationId);
        ledger.Validate(firstCompleted);
        ledger.Observe(firstCompleted);
        Assert.Throws<InvalidOperationException>(() => ledger.ThrowIfActive());
        Assert.Throws<InvalidOperationException>(() => ledger.Validate(RootTerminal(ExecutionEventKinds.Terminated)));

        var secondCompleted = SequenceEvent(ExecutionEventKinds.SequenceOutputCompleted, "scope-a", "second", 1,
            second.InvocationId, ReadEvidence(second).ActivationId);
        ledger.Validate(secondCompleted);
        ledger.Observe(secondCompleted);
        ledger.Validate(RootTerminal(ExecutionEventKinds.Completed));
        ledger.ThrowIfActive();
    }

    [Fact]
    public void Skipped_sequence_output_does_not_create_active_work()
    {
        var ledger = new ExecutionActivationLedger();
        var skipped = SequenceEvent(ExecutionEventKinds.SequenceOutputSkipped, "scope-a", "skipped", 0,
            activationId: null);

        ledger.Validate(skipped);
        ledger.Observe(skipped);
        ledger.ThrowIfActive();
        ledger.Validate(RootTerminal(ExecutionEventKinds.Completed));
    }

    [Fact]
    public void Loop_iteration_remains_active_until_matching_scoped_iteration_settles()
    {
        var ledger = new ExecutionActivationLedger();
        var started = LoopEvent(ExecutionEventKinds.LoopIterationStarted, "loop", "outer/loop/0", 1);
        var wrongIteration = LoopEvent(ExecutionEventKinds.LoopIterationCompleted, "loop", "outer/loop/0", 2);
        var completed = LoopEvent(ExecutionEventKinds.LoopIterationCompleted, "loop", "outer/loop/0", 1);

        ledger.Validate(started);
        ledger.Observe(started);
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Validate(RootTerminal(ExecutionEventKinds.Completed)));
        Assert.Throws<InvalidOperationException>(() => ledger.Validate(wrongIteration));

        // A loop node is invoked again for its return edge; the lifecycle
        // invocation ID can change while the activation scope remains stable.
        ledger.Validate(completed);
        ledger.Observe(completed);
        ledger.ThrowIfActive();
        ledger.Validate(RootTerminal(ExecutionEventKinds.Completed));
    }

    [Fact]
    public void Failed_root_can_preserve_an_unsettled_loop_iteration_as_incomplete_evidence()
    {
        var ledger = new ExecutionActivationLedger();
        var started = LoopEvent(ExecutionEventKinds.LoopIterationStarted, "loop", null, 1);
        ledger.Validate(started);
        ledger.Observe(started);

        ledger.Validate(RootTerminal(ExecutionEventKinds.Failed));
        Assert.Throws<InvalidOperationException>(() => ledger.ThrowIfActive());
    }

    private static ExecutionEvent SequenceEvent(string kind, string scope, string output, int ordinal,
        Guid? sequenceInvocation = null, Guid? activationId = null)
    {
        var invocation = sequenceInvocation ?? Guid.NewGuid();
        var evidence = new SequenceLifecycleEvidence(invocation, "sequence", output, ordinal,
            kind == ExecutionEventKinds.SequenceOutputSkipped ? null : activationId ?? Guid.NewGuid(),
            $"connection-{output}");
        return new(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, kind, "sequence", invocation,
            evidence.ConnectionId, SequenceEvidence.Serialize(evidence)) { ScopeId = scope };
    }

    private static SequenceLifecycleEvidence ReadEvidence(ExecutionEvent evt)
    {
        Assert.True(SequenceEvidence.TryRead(evt, out var evidence));
        return evidence!;
    }

    private static ExecutionEvent RootTerminal(string kind) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, kind);

    private static ExecutionEvent Event(string kind, string nodeId, Guid invocationId, string? scopeId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, kind, nodeId, invocationId) { ScopeId = scopeId };

    private static ExecutionEvent LoopEvent(string kind, string nodeId, string? scopeId, int iteration) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, kind, nodeId, Guid.NewGuid(),
            Data: JsonSerializer.Serialize(new { iteration })) { ScopeId = scopeId };
}
