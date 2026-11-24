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
    public void Event_delivery_advances_only_after_each_event_is_accepted()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["first"],
            new Dictionary<string, string> { ["first"] = "connection-a" }, allowSingleOutput: true);
        var startBatch = coordinator.BeginEventDelivery();

        Assert.Single(startBatch.Events);
        Assert.Equal(ExecutionEventKinds.SequenceStarted, startBatch.Events[0].Kind);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginEventDelivery());

        coordinator.AcceptDeliveredEvent(startBatch.DeliveryId, startBatch.Events[0]);
        var activationId = Guid.NewGuid();
        coordinator.DispatchNext(activationId);
        coordinator.Complete(activationId, SequenceTerminalState.Completed);

        var settlementBatch = coordinator.BeginEventDelivery();
        Assert.Equal(new[]
        {
            ExecutionEventKinds.SequenceOutputDispatched,
            ExecutionEventKinds.SequenceOutputCompleted,
            ExecutionEventKinds.SequenceCompleted
        }, settlementBatch.Events.Select(evt => evt.Kind));
        coordinator.AcceptDeliveredEvent(settlementBatch.DeliveryId, settlementBatch.Events[0]);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginEventDelivery());
        coordinator.AcceptDeliveredEvent(settlementBatch.DeliveryId, settlementBatch.Events[1]);
        coordinator.AcceptDeliveredEvent(settlementBatch.DeliveryId, settlementBatch.Events[2]);

        var emptyBatch = coordinator.BeginEventDelivery();
        Assert.Equal(Guid.Empty, emptyBatch.DeliveryId);
        Assert.Empty(emptyBatch.Events);
    }

    [Fact]
    public void Recorder_failure_marks_staged_sequence_delivery_uncertain_and_prevents_replay()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["first"],
            new Dictionary<string, string> { ["first"] = "connection-a" }, allowSingleOutput: true);
        var initial = coordinator.BeginEventDelivery();
        coordinator.AcceptDeliveredEvent(initial.DeliveryId, initial.Events[0]);
        var activationId = Guid.NewGuid();
        coordinator.DispatchNext(activationId);
        coordinator.Complete(activationId, SequenceTerminalState.Completed);
        var settlement = coordinator.BeginEventDelivery();

        coordinator.AcceptDeliveredEvent(settlement.DeliveryId, settlement.Events[0]);
        coordinator.MarkEventDeliveryUncertain(settlement.DeliveryId);

        var retry = Assert.Throws<InvalidOperationException>(() => coordinator.BeginEventDelivery());
        Assert.Contains("reconcile persisted history", retry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_delivery_rejects_acknowledging_an_out_of_order_fact()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["first"],
            new Dictionary<string, string> { ["first"] = "connection-a" }, allowSingleOutput: true);
        var batch = coordinator.BeginEventDelivery();

        Assert.Throws<InvalidOperationException>(() => coordinator.AcceptDeliveredEvent(batch.DeliveryId,
            batch.Events[0] with { Kind = ExecutionEventKinds.SequenceFailed }));
        coordinator.AcceptDeliveredEvent(batch.DeliveryId, batch.Events[0]);
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
    public void Attributed_break_is_recorded_before_skips_and_matches_the_dispatched_output()
    {
        const string scope = "array-loop/0";
        var sequenceId = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var sourceFrameId = Guid.NewGuid();
        var targetFrameId = Guid.NewGuid();
        var coordinator = new SequenceRuntimeCoordinator(sequenceId, "sequence", ["first", "suffix"],
            new Dictionary<string, string> { ["first"] = "connection-a", ["suffix"] = "connection-b" },
            scopeId: scope);
        coordinator.DispatchNext(activationId);
        var lineage = new WorkflowRunner.SequenceMessageLineage(sequenceId, "sequence", "first", 0,
            activationId, "connection-a", scope);
        var request = new WorkflowRunner.StructuredActivationBreakRequest("break", sourceFrameId, scope);

        coordinator.AcceptBreak(lineage, request, targetFrameId);

        Assert.Equal([ExecutionEventKinds.SequenceStarted, ExecutionEventKinds.SequenceOutputDispatched,
            ExecutionEventKinds.SequenceBreakAccepted, ExecutionEventKinds.SequenceOutputSkipped],
            coordinator.Events.Select(evt => evt.Kind));
        Assert.Throws<InvalidOperationException>(() => coordinator.AcceptBreak(
            lineage with { ConnectionId = "tampered" }, request, targetFrameId));
        var recorded = coordinator.ToExecutionEvents(Guid.NewGuid(), Guid.NewGuid(), 0, DateTimeOffset.UtcNow);
        Assert.All(recorded, evt => Assert.Equal(scope, evt.ScopeId));
        Assert.True(SequenceBreakAcceptanceEvidence.TryRead(recorded[2], out var evidence));
        Assert.Equal(new SequenceBreakAcceptanceEvidence(sequenceId, "sequence", "break", sourceFrameId,
            targetFrameId, scope), evidence);
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
            }, scopeId: "array-loop/0");

        coordinator.DispatchNext(firstActivation);
        coordinator.DispatchNext(secondActivation);
        coordinator.Complete(firstActivation, SequenceTerminalState.Completed);
        coordinator.AcceptBreak(new(sequenceInvocationId, "sequence", "second", 1, secondActivation,
                "connection-b", "array-loop/0"),
            new("break", Guid.NewGuid(), "array-loop/0"), frameId);
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
        Assert.True(signal.Matches(signal.LastSettledOutput, frameId));
        Assert.False(signal.Matches(signal.LastSettledOutput with { OutputId = "other-output" }, frameId));
        Assert.False(signal.Matches(signal.LastSettledOutput, Guid.NewGuid()));
        Assert.Equal([SequenceActivationState.Completed, SequenceActivationState.Completed, SequenceActivationState.Skipped],
            signal.OutputActivations.Select(output => output.State));
        Assert.Throws<InvalidOperationException>(() => SequenceAggregateCompletionSignal.Create(coordinator,
            new(Guid.NewGuid(), "other-sequence", "second", 1, secondActivation, "connection-b", "array-loop/0"), frameId));
        Assert.Throws<InvalidOperationException>(() => SequenceAggregateCompletionSignal.Create(coordinator,
            new(sequenceInvocationId, "sequence", "second", 1, secondActivation, "wrong-connection", "array-loop/0"), frameId));
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
                }, scopeId: "array-loop/0");
            var first = Guid.NewGuid();
            coordinator.DispatchNext(first);
            var firstLineage = new WorkflowRunner.SequenceMessageLineage(coordinator.SequenceInvocationId,
                coordinator.SequenceNodeId, "first", 0, first, "connection-a", "sequence/first");
            var acceptedBreakRequest = new WorkflowRunner.StructuredActivationBreakRequest("break", Guid.NewGuid(),
                "array-loop/0");
            var targetFrame = Guid.NewGuid();
            using var start = new Barrier(3);

            var admission = Task.Run(() =>
            {
                start.SignalAndWait();
                return coordinator.TryDispatchNext(Guid.NewGuid(), maximumActiveOutputs: 2);
            });
            var breakAcceptance = Task.Run(() =>
            {
                start.SignalAndWait();
                coordinator.AcceptBreak(firstLineage, acceptedBreakRequest, targetFrame);
            });
            start.SignalAndWait();
            var admissionResult = await admission;
            await breakAcceptance;

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

    private static SequenceRuntimeCoordinator Drained(params string[] outputs) =>
        new(Guid.NewGuid(), "sequence", outputs,
            outputs.ToDictionary(id => id, id => $"connection-{id}"), allowSingleOutput: false)
        { DrainInFlightOutputs = true };

    private static string[] Kinds(SequenceRuntimeCoordinator coordinator) =>
        coordinator.Events.Select(evt => $"{evt.Kind}:{evt.OutputId}").ToArray();

    [Fact]
    public void Drained_failure_leaves_a_dispatched_sibling_open_until_it_reports()
    {
        var coordinator = Drained("first", "second");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);

        coordinator.Complete(first, SequenceTerminalState.Failed);

        Assert.False(coordinator.IsTerminal);
        Assert.DoesNotContain(coordinator.Events, evt => evt.Kind is ExecutionEventKinds.SequenceFailed or
            ExecutionEventKinds.SequenceOutputCancelled);

        coordinator.Complete(second, SequenceTerminalState.Cancelled);

        Assert.True(coordinator.IsTerminal);
        Assert.Equal(["sequence.output.failed:first", "sequence.output.cancelled:second", "sequence.failed:"],
            Kinds(coordinator).Skip(3).ToArray());
    }

    [Fact]
    public void Drained_sibling_that_completes_after_a_failure_records_its_real_outcome()
    {
        var coordinator = Drained("first", "second");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);
        coordinator.Complete(first, SequenceTerminalState.Failed);

        coordinator.Complete(second, SequenceTerminalState.Completed);

        Assert.Contains("sequence.output.completed:second", Kinds(coordinator));
        Assert.DoesNotContain(coordinator.Events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled);
        Assert.Equal(ExecutionEventKinds.SequenceFailed, coordinator.Events[^1].Kind);
    }

    [Fact]
    public void Drained_failure_skips_outputs_that_never_started_immediately()
    {
        var coordinator = Drained("first", "second", "third");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);

        coordinator.Complete(first, SequenceTerminalState.Failed);

        Assert.Contains("sequence.output.skipped:third", Kinds(coordinator));
        Assert.False(coordinator.IsTerminal);
    }

    [Fact]
    public void Abandoning_settles_only_outputs_that_never_reported_and_repeats_nothing()
    {
        var coordinator = Drained("first", "second", "third");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);
        coordinator.DispatchNext(third);
        coordinator.Complete(first, SequenceTerminalState.Cancelled);
        coordinator.CancelPending();
        Assert.False(coordinator.IsTerminal);

        coordinator.AbandonUnsettled();
        coordinator.AbandonUnsettled();

        var kinds = Kinds(coordinator);
        Assert.True(coordinator.IsTerminal);
        Assert.Equal(1, kinds.Count(kind => kind == "sequence.output.cancelled:first"));
        Assert.Equal(1, kinds.Count(kind => kind == "sequence.output.cancelled:second"));
        Assert.Equal(1, kinds.Count(kind => kind == "sequence.output.cancelled:third"));
        Assert.Single(kinds, kind => kind == "sequence.cancelled:");
        Assert.Equal(ExecutionEventKinds.SequenceCancelled, coordinator.Events[^1].Kind);
    }

    [Fact]
    public void Drained_host_cancel_waits_for_dispatched_outputs_then_terminates_cancelled()
    {
        var coordinator = Drained("first", "second");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);

        coordinator.CancelPending();
        Assert.False(coordinator.IsTerminal);
        coordinator.Complete(first, SequenceTerminalState.Cancelled);
        Assert.False(coordinator.IsTerminal);
        coordinator.Complete(second, SequenceTerminalState.Cancelled);

        Assert.True(coordinator.IsTerminal);
        Assert.Equal(ExecutionEventKinds.SequenceCancelled, coordinator.Events[^1].Kind);
    }

    [Fact]
    public void Without_drain_a_failure_still_cascade_cancels_a_dispatched_sibling()
    {
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["first", "second"],
            new Dictionary<string, string> { ["first"] = "connection-first", ["second"] = "connection-second" });
        var first = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(Guid.NewGuid());

        coordinator.Complete(first, SequenceTerminalState.Failed);

        Assert.True(coordinator.IsTerminal);
        Assert.Contains("sequence.output.cancelled:second", Kinds(coordinator));
    }

    [Fact]
    public void Abandoning_records_unreported_outputs_as_abandoned_and_ends_the_sequence_failed()
    {
        var coordinator = Drained("first", "second", "third");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);
        coordinator.Complete(first, SequenceTerminalState.Failed);

        coordinator.AbandonOutputs();

        var kinds = Kinds(coordinator);
        Assert.Contains("sequence.output.abandoned:second", kinds);
        Assert.Contains("sequence.output.skipped:third", kinds);
        Assert.DoesNotContain(kinds, kind => kind.StartsWith("sequence.output.cancelled", StringComparison.Ordinal));
        Assert.True(coordinator.IsTerminal);
        Assert.Equal(ExecutionEventKinds.SequenceFailed, coordinator.Events[^1].Kind);
    }

    [Fact]
    public void A_late_report_after_abandoning_is_ignored_and_abandoning_again_repeats_nothing()
    {
        var coordinator = Drained("first", "second");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);
        coordinator.Complete(first, SequenceTerminalState.Failed);
        coordinator.AbandonOutputs();
        var before = coordinator.Events.Count;

        coordinator.Complete(second, SequenceTerminalState.Completed);
        coordinator.AbandonOutputs();

        Assert.Equal(before, coordinator.Events.Count);
        Assert.DoesNotContain("sequence.output.completed:second", Kinds(coordinator));
        Assert.Single(Kinds(coordinator), kind => kind == "sequence.failed:");
    }

    [Fact]
    public void An_abandoned_output_ends_the_sequence_failed_even_after_a_host_cancel()
    {
        var coordinator = Drained("first", "second");
        coordinator.DispatchNext(Guid.NewGuid());
        coordinator.DispatchNext(Guid.NewGuid());
        coordinator.CancelPending();

        coordinator.AbandonOutputs();

        Assert.Equal(["sequence.output.abandoned:first", "sequence.output.abandoned:second", "sequence.failed:"],
            Kinds(coordinator).Skip(3).ToArray());
    }

    [Fact]
    public void The_abandonable_stop_signal_fires_once_for_a_failure_or_cancel_and_never_for_success()
    {
        var signals = 0;
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["first", "second"],
            new Dictionary<string, string> { ["first"] = "c1", ["second"] = "c2" })
        {
            DrainInFlightOutputs = true,
            AbandonableStop = () => signals++
        };
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        coordinator.DispatchNext(first);
        coordinator.DispatchNext(second);

        coordinator.Complete(first, SequenceTerminalState.Completed);
        Assert.Equal(0, signals);

        coordinator.Complete(second, SequenceTerminalState.Failed);
        coordinator.CancelPending();
        Assert.Equal(1, signals);
    }
}
