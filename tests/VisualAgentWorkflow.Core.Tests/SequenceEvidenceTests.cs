using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceEvidenceTests
{
    [Fact]
    public void Lifecycle_evidence_round_trips_with_stable_identity()
    {
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sequenceInvocationId = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var evidence = new SequenceLifecycleEvidence(sequenceInvocationId, "sequence", "first", 0,
            activationId, "connection-a");
        var evt = new ExecutionEvent(runId, revisionId, 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.SequenceOutputDispatched, "sequence", sequenceInvocationId,
            "connection-a", SequenceEvidence.Serialize(evidence));

        Assert.True(SequenceEvidence.TryRead(evt, out var parsed));
        Assert.Equal(evidence, parsed);
    }

    [Fact]
    public void Playback_reader_rejects_malformed_or_wrong_version_evidence()
    {
        var evt = new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.SequenceOutputCompleted, "sequence", Guid.NewGuid(), Data: "not-json");
        Assert.False(SequenceEvidence.TryRead(evt, out _));

        var wrongVersion = JsonSerializer.Serialize(new SequenceLifecycleEvidence(Guid.NewGuid(), "sequence", null, null, null, null, 2));
        Assert.False(SequenceEvidence.TryRead(evt with { Data = wrongVersion }, out _));
    }

    [Fact]
    public void Non_sequence_events_are_not_interpreted_as_sequence_evidence()
    {
        var evt = new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.NodeCompleted, "node", Guid.NewGuid(), Data: "{}");

        Assert.False(SequenceEvidence.TryRead(evt, out _));
        Assert.False(SequenceEvidence.IsLifecycleEvent(ExecutionEventKinds.NodeCompleted));
    }

    [Fact]
    public void Playback_reconstructs_completed_outputs_without_live_execution()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var firstActivation = Guid.NewGuid();
        var secondActivation = Guid.NewGuid();
        var events = new[]
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "first", 0, firstActivation, "connection-a")),
            Lifecycle(runId, revisionId, 3, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "first", 0, firstActivation, "connection-a")),
            Lifecycle(runId, revisionId, 4, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "second", 1, secondActivation, "connection-b")),
            Lifecycle(runId, revisionId, 5, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "second", 1, secondActivation, "connection-b")),
            Lifecycle(runId, revisionId, 6, ExecutionEventKinds.SequenceCompleted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null))
        };

        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var snapshot));
        Assert.Equal("completed", snapshot!.Status);
        Assert.Equal(["completed", "completed"], snapshot.Outputs.Select(output => output.State));
        Assert.Equal(["first", "second"], snapshot.Outputs.Select(output => output.OutputId));
    }

    [Fact]
    public void Playback_reconstructs_running_prefix_and_cancelled_skips()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activation = Guid.NewGuid();
        var prefix = new[]
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "first", 0, activation, "connection-a"))
        };
        Assert.True(SequenceEvidence.TryReconstructPlayback(prefix, out var running));
        Assert.Equal("running", running!.Status);
        Assert.Equal("dispatched", Assert.Single(running.Outputs).State);

        var cancelled = prefix.Concat(new[]
        {
            Lifecycle(runId, revisionId, 3, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "first", 0, activation, "connection-a")),
            Lifecycle(runId, revisionId, 4, ExecutionEventKinds.SequenceOutputSkipped, sequenceId,
                new(sequenceId, "sequence", "second", 1, null, "connection-b")),
            Lifecycle(runId, revisionId, 5, ExecutionEventKinds.SequenceCancelled, sequenceId,
                new(sequenceId, "sequence", null, null, null, null))
        }).ToArray();
        Assert.True(SequenceEvidence.TryReconstructPlayback(cancelled, out var cancelledSnapshot));
        Assert.Equal("cancelled", cancelledSnapshot!.Status);
        Assert.Equal("skipped", cancelledSnapshot.Outputs[1].State);
    }

    [Fact]
    public void Playback_reconstructs_overlapping_outputs_that_complete_out_of_order()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var firstActivation = Guid.NewGuid();
        var secondActivation = Guid.NewGuid();
        var events = new[]
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "first", 0, firstActivation, "connection-a")),
            Lifecycle(runId, revisionId, 3, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "second", 1, secondActivation, "connection-b")),
            Lifecycle(runId, revisionId, 4, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "second", 1, secondActivation, "connection-b")),
            Lifecycle(runId, revisionId, 5, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "first", 0, firstActivation, "connection-a")),
            Lifecycle(runId, revisionId, 6, ExecutionEventKinds.SequenceCompleted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null))
        };

        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var snapshot));
        Assert.Equal(["completed", "completed"], snapshot!.Outputs.Select(output => output.State));
    }

    [Fact]
    public void Playback_rejects_out_of_order_dispatch_and_events_after_terminal()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var invalidDispatch = new[]
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "second", 1, Guid.NewGuid(), "connection-b"))
        };
        Assert.False(SequenceEvidence.TryReconstructPlayback(invalidDispatch, out _));

        var terminal = Lifecycle(runId, revisionId, 3, ExecutionEventKinds.SequenceCompleted, sequenceId,
            new(sequenceId, "sequence", null, null, null, null));
        var afterTerminal = invalidDispatch.Take(1).Append(terminal).ToArray();
        Assert.False(SequenceEvidence.TryReconstructPlayback(afterTerminal, out _));
    }

    private static ExecutionEvent Lifecycle(Guid runId, Guid revisionId, long sequence, string kind, Guid invocationId,
        SequenceLifecycleEvidence evidence) => new(runId, revisionId, sequence, DateTimeOffset.UtcNow, kind,
        evidence.SequenceNodeId, invocationId, evidence.ConnectionId, SequenceEvidence.Serialize(evidence));
}
