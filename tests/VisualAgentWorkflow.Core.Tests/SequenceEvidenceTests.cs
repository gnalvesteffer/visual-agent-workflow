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
    public void Agent_playback_reconstructs_nested_lineage_without_runtime_services()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var agentInvocationId = Guid.NewGuid();
        var modelCallId = Guid.NewGuid();
        var events = new List<ExecutionEvent>
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "research", 0, activationId, "sequence-research")),
            Descendant(runId, revisionId, 3, ExecutionEventKinds.AgentStarted, "agent", agentInvocationId,
                Lineage(sequenceId, "research", 0, activationId, "sequence-research")),
            Descendant(runId, revisionId, 4, ExecutionEventKinds.ModelRequested, "agent", agentInvocationId,
                Lineage(sequenceId, "research", 0, activationId, "sequence-research", modelCallId)),
            Descendant(runId, revisionId, 5, ExecutionEventKinds.ModelResponded, "agent", agentInvocationId,
                Lineage(sequenceId, "research", 0, activationId, "sequence-research", modelCallId)),
            Descendant(runId, revisionId, 6, ExecutionEventKinds.AgentCompleted, "agent", agentInvocationId,
                Lineage(sequenceId, "research", 0, activationId, "sequence-research")),
            Lifecycle(runId, revisionId, 7, ExecutionEventKinds.SequenceOutputCompleted, sequenceId,
                new(sequenceId, "sequence", "research", 0, activationId, "sequence-research")),
            Lifecycle(runId, revisionId, 8, ExecutionEventKinds.SequenceCompleted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null))
        };

        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Sequence.Status);
        var agent = Assert.Single(playback.Agents);
        Assert.Equal("completed", agent.State);
        Assert.Equal("research", agent.OutputId);
        var model = Assert.Single(playback.ModelCalls);
        Assert.Equal(modelCallId, model.ModelCallId);
        Assert.Equal("completed", model.State);
    }

    [Fact]
    public void Agent_playback_rejects_tampered_nested_lineage()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var agentInvocationId = Guid.NewGuid();
        var events = new List<ExecutionEvent>
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "research", 0, activationId, "sequence-research")),
            Descendant(runId, revisionId, 3, ExecutionEventKinds.AgentStarted, "agent", agentInvocationId,
                Lineage(sequenceId, "research", 0, activationId, "tampered-connection"))
        };

        Assert.False(SequenceAgentEvidence.TryReconstructPlayback(events, out _));
    }

    [Fact]
    public void Agent_playback_preserves_failed_and_cancelled_terminal_outcomes()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var failedActivation = Guid.NewGuid();
        var cancelledActivation = Guid.NewGuid();
        var failedInvocation = Guid.NewGuid();
        var cancelledInvocation = Guid.NewGuid();
        var events = new List<ExecutionEvent>
        {
            Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
                new(sequenceId, "sequence", null, null, null, null)),
            Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "failed", 0, failedActivation, "connection-failed")),
            Descendant(runId, revisionId, 3, ExecutionEventKinds.AgentStarted, "failed-agent", failedInvocation,
                Lineage(sequenceId, "failed", 0, failedActivation, "connection-failed")),
            Descendant(runId, revisionId, 4, ExecutionEventKinds.AgentFailed, "failed-agent", failedInvocation,
                Lineage(sequenceId, "failed", 0, failedActivation, "connection-failed", outcome: "provider_error")),
            Lifecycle(runId, revisionId, 5, ExecutionEventKinds.SequenceOutputFailed, sequenceId,
                new(sequenceId, "sequence", "failed", 0, failedActivation, "connection-failed")),
            Lifecycle(runId, revisionId, 6, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
                new(sequenceId, "sequence", "cancelled", 1, cancelledActivation, "connection-cancelled")),
            Descendant(runId, revisionId, 7, ExecutionEventKinds.AgentStarted, "cancelled-agent", cancelledInvocation,
                Lineage(sequenceId, "cancelled", 1, cancelledActivation, "connection-cancelled")),
            Descendant(runId, revisionId, 8, ExecutionEventKinds.AgentFailed, "cancelled-agent", cancelledInvocation,
                Lineage(sequenceId, "cancelled", 1, cancelledActivation, "connection-cancelled", outcome: "cancelled")),
            Lifecycle(runId, revisionId, 9, ExecutionEventKinds.SequenceOutputCancelled, sequenceId,
                new(sequenceId, "sequence", "cancelled", 1, cancelledActivation, "connection-cancelled")),
            Lifecycle(runId, revisionId, 10, ExecutionEventKinds.SequenceFailed, sequenceId,
                new(sequenceId, "sequence", null, null, null, null))
        };

        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal(["failed", "cancelled"], playback!.Agents.OrderBy(agent => agent.Ordinal).Select(agent => agent.State));
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

    [Fact]
    public void Playback_rejects_tampered_invocation_or_connection_identity()
    {
        var sequenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activationId = Guid.NewGuid();
        var started = Lifecycle(runId, revisionId, 1, ExecutionEventKinds.SequenceStarted, sequenceId,
            new(sequenceId, "sequence", null, null, null, null));
        var dispatched = Lifecycle(runId, revisionId, 2, ExecutionEventKinds.SequenceOutputDispatched, sequenceId,
            new(sequenceId, "sequence", "first", 0, activationId, "connection-a"));

        Assert.False(SequenceEvidence.TryReconstructPlayback(
            [started, dispatched with { InvocationId = Guid.NewGuid() }], out _));
        Assert.False(SequenceEvidence.TryReconstructPlayback(
            [started, dispatched with { ConnectionId = "connection-tampered" }], out _));
    }

    private static ExecutionEvent Lifecycle(Guid runId, Guid revisionId, long sequence, string kind, Guid invocationId,
        SequenceLifecycleEvidence evidence) => new(runId, revisionId, sequence, DateTimeOffset.UtcNow, kind,
        evidence.SequenceNodeId, invocationId, evidence.ConnectionId, SequenceEvidence.Serialize(evidence));

    private static ExecutionEvent Descendant(Guid runId, Guid revisionId, long sequence, string kind, string nodeId,
        Guid invocationId, string data) => new(runId, revisionId, sequence, DateTimeOffset.UtcNow, kind,
        nodeId, invocationId, Data: data);

    private static string Lineage(Guid sequenceId, string outputId, int ordinal, Guid activationId,
        string connectionId, Guid? modelCallId = null, string? outcome = null)
    {
        var lineage = new
        {
            sequenceInvocationId = sequenceId,
            sequenceNodeId = "sequence",
            outputId,
            ordinal,
            outputActivationId = activationId,
            connectionId
        };
        return JsonSerializer.Serialize(new { sequenceLineage = lineage, modelCallId, outcome });
    }
}
