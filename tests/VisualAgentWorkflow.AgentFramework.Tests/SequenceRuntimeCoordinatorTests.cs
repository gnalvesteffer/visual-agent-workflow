using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SequenceRuntimeCoordinatorTests
{
    [Fact]
    public void Lifecycle_events_preserve_output_order_and_identity()
    {
        var sequenceInvocationId = Guid.NewGuid();
        var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, "sequence",
            ["first", "second"],
            new Dictionary<string, string> { ["first"] = "connection-a", ["second"] = "connection-b" });
        var activationId = Guid.NewGuid();

        coordinator.DispatchNext(activationId);
        coordinator.Complete(activationId, SequenceTerminalState.Completed);

        var events = coordinator.Events;
        Assert.Equal(ExecutionEventKinds.SequenceStarted, events[0].Kind);
        Assert.Equal(["first", "first"], events.Skip(1).Select(evt => evt.OutputId));
        Assert.All(events, evt => Assert.Equal(sequenceInvocationId, evt.SequenceInvocationId));
        Assert.Equal("connection-a", events[1].ConnectionId);
        Assert.Equal(0, events[1].Ordinal);
    }

    [Fact]
    public void Failure_records_later_outputs_as_skipped()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence",
            ["first", "second", "third"],
            new Dictionary<string, string>
            {
                ["first"] = "connection-a", ["second"] = "connection-b", ["third"] = "connection-c"
            });
        var activationId = Guid.NewGuid();

        coordinator.DispatchNext(activationId);
        coordinator.Complete(activationId, SequenceTerminalState.Failed);

        Assert.Equal([ExecutionEventKinds.SequenceOutputFailed, ExecutionEventKinds.SequenceOutputSkipped,
            ExecutionEventKinds.SequenceOutputSkipped, ExecutionEventKinds.SequenceFailed],
            coordinator.Events.Skip(2).Select(evt => evt.Kind));
        Assert.Equal(["second", "third"], coordinator.Events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped).Select(evt => evt.OutputId));
    }

    [Fact]
    public void Failure_cancels_other_active_outputs_before_sequence_failure()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence",
            ["first", "second", "third"],
            new Dictionary<string, string>
            {
                ["first"] = "connection-a", ["second"] = "connection-b", ["third"] = "connection-c"
            });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);
        coordinator.Complete(first, SequenceTerminalState.Failed);

        Assert.Equal([
            ExecutionEventKinds.SequenceOutputFailed,
            ExecutionEventKinds.SequenceOutputCancelled,
            ExecutionEventKinds.SequenceOutputSkipped,
            ExecutionEventKinds.SequenceFailed
        ], coordinator.Events.Skip(3).Select(evt => evt.Kind));
        Assert.Equal("second", coordinator.Events.Single(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled).OutputId);
        Assert.Equal("third", coordinator.Events.Single(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped).OutputId);
    }

    [Fact]
    public void Lifecycle_events_can_be_recorded_as_passive_execution_events()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence",
            ["first", "second"],
            new Dictionary<string, string> { ["first"] = "connection-a", ["second"] = "connection-b" });
        var activationId = Guid.NewGuid();
        coordinator.DispatchNext(activationId);
        coordinator.Complete(activationId, SequenceTerminalState.Completed);

        var recorded = coordinator.ToExecutionEvents(Guid.NewGuid(), Guid.NewGuid(), 12, DateTimeOffset.UtcNow);
        Assert.Equal(12, recorded[0].Sequence);
        Assert.All(recorded, evt => Assert.True(SequenceEvidence.TryRead(evt, out _)));
        Assert.Equal(ExecutionEventKinds.SequenceOutputDispatched, recorded[1].Kind);
    }
}
