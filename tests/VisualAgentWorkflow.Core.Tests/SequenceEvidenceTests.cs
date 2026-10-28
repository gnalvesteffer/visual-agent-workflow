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
}
