using VisualAgentWorkflow.AgentFramework;
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
        Assert.Equal("sequence.started", events[0].Kind);
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

        Assert.Equal(["sequence.output.failed", "sequence.output.skipped", "sequence.output.skipped", "sequence.failed"],
            coordinator.Events.Skip(2).Select(evt => evt.Kind));
        Assert.Equal(["second", "third"], coordinator.Events.Where(evt => evt.Kind == "sequence.output.skipped").Select(evt => evt.OutputId));
    }
}
