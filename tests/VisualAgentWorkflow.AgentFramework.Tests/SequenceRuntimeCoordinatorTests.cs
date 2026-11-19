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
    public void Break_closes_admission_and_waits_for_dispatched_outputs_to_settle()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence",
            ["first", "second", "third"],
            new Dictionary<string, string>
            {
                ["first"] = "connection-a", ["second"] = "connection-b", ["third"] = "connection-c"
            });
        var firstActivation = Guid.NewGuid();

        coordinator.DispatchNext(firstActivation);
        coordinator.StopAdmission();

        Assert.False(coordinator.IsTerminal);
        Assert.Equal(["second", "third"], coordinator.Events
            .Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped)
            .Select(evt => evt.OutputId));
        Assert.DoesNotContain(coordinator.Events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.Throws<InvalidOperationException>(() => coordinator.DispatchNext(Guid.NewGuid()));

        coordinator.Complete(firstActivation, SequenceTerminalState.Completed);

        Assert.True(coordinator.IsTerminal);
        Assert.Equal(ExecutionEventKinds.SequenceCompleted, coordinator.Events[^1].Kind);
    }

    [Fact]
    public void Terminal_sequence_exposes_typed_aggregate_signal_with_frame_and_output_lineage()
    {
        var sequenceInvocationId = Guid.NewGuid();
        var frameId = Guid.NewGuid();
        var firstActivation = Guid.NewGuid();
        var secondActivation = Guid.NewGuid();
        var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, "sequence",
            ["first", "second", "suffix"],
            new Dictionary<string, string>
            {
                ["first"] = "connection-a", ["second"] = "connection-b", ["suffix"] = "connection-c"
            });

        coordinator.DispatchNext(firstActivation);
        coordinator.DispatchNext(secondActivation);
        coordinator.Complete(firstActivation, SequenceTerminalState.Completed);
        coordinator.StopAdmission(successfulBreak: true);
        Assert.Null(SequenceAggregateCompletionSignal.Create(coordinator,
            new(sequenceInvocationId, "sequence", "first", 0, firstActivation, "connection-a", "array-loop/0"), frameId));

        coordinator.Complete(secondActivation, SequenceTerminalState.Completed);
        var signal = SequenceAggregateCompletionSignal.Create(coordinator,
            new(sequenceInvocationId, "sequence", "second", 1, secondActivation, "connection-b", "array-loop/0"), frameId);

        Assert.NotNull(signal);
        Assert.Equal(sequenceInvocationId, signal.SequenceInvocationId);
        Assert.Equal("sequence", signal.SequenceNodeId);
        Assert.Equal("array-loop/0", signal.ScopeId);
        Assert.Equal(frameId, signal.FrameId);
        Assert.Equal(ExecutionEventKinds.SequenceBreakCompleted, signal.TerminalEventKind);
        Assert.Equal(new SequenceOutputActivationLineage(sequenceInvocationId, "sequence", "second", 1,
                secondActivation, "connection-b", "array-loop/0"),
            signal.LastSettledOutput);
        Assert.Equal([SequenceActivationState.Completed, SequenceActivationState.Completed, SequenceActivationState.Skipped],
            signal.OutputActivations.Select(output => output.State));
        Assert.Throws<InvalidOperationException>(() => SequenceAggregateCompletionSignal.Create(coordinator,
            new(Guid.NewGuid(), "other-sequence", "second", 1, secondActivation, "connection-b", "array-loop/0"), frameId));
    }

    [Fact]
    public async Task Admission_racing_with_break_is_atomic_and_cannot_dispatch_after_suffix_skip()
    {
        const int iterations = 128;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence",
                ["first", "racing", "suffix"],
                new Dictionary<string, string>
                {
                    ["first"] = "connection-a", ["racing"] = "connection-b", ["suffix"] = "connection-c"
                });
            var first = Guid.NewGuid();
            coordinator.DispatchNext(first);
            using var start = new Barrier(3);

            var admission = Task.Run(() =>
            {
                start.SignalAndWait();
                return coordinator.TryDispatchNext(Guid.NewGuid(), maximumActiveOutputs: 2);
            });
            var breakRequest = Task.Run(() =>
            {
                start.SignalAndWait();
                coordinator.StopAdmission(successfulBreak: true);
            });
            start.SignalAndWait();
            var admissionResult = await admission;
            await breakRequest;

            Assert.Null(coordinator.TryDispatchNext(Guid.NewGuid(), maximumActiveOutputs: 2));
            var prefix = coordinator.Events;
            var dispatched = prefix.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched).ToArray();
            Assert.InRange(dispatched.Length, 1, 2);
            Assert.Equal(dispatched.Length, dispatched.Select(evt => evt.OutputId).Distinct(StringComparer.Ordinal).Count());
            var firstSkippedIndex = Array.FindIndex(prefix.ToArray(), evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped);
            Assert.True(firstSkippedIndex >= 0);
            Assert.DoesNotContain(prefix.Skip(firstSkippedIndex + 1),
                evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched);
            Assert.Equal(admissionResult is null ? ["racing", "suffix"] : ["suffix"], prefix
                .Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped).Select(evt => evt.OutputId));
            Assert.Equal(admissionResult is null, prefix.All(evt => evt.Kind != ExecutionEventKinds.SequenceOutputDispatched ||
                evt.OutputId != "racing"));

            coordinator.Complete(first, SequenceTerminalState.Completed);
            if (admissionResult is { } racing)
                coordinator.Complete(racing.ActivationId!.Value, SequenceTerminalState.Completed);
            Assert.Equal(ExecutionEventKinds.SequenceBreakCompleted, coordinator.Events[^1].Kind);
        }
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
