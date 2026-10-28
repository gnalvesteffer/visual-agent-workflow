using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class VariableEvidenceTests
{
    [Fact]
    public void Playback_reconstructs_writes_and_reads_from_a_prefix_without_execution()
    {
        var run = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var events = new[]
        {
            Event(1, ExecutionEventKinds.VariableWritten, "set", new { variableId = "count", dataType = "integer", previousValue = 1, value = 2, version = 2L }),
            Event(2, ExecutionEventKinds.VariableRead, "get", new { variableId = "count", dataType = "integer", value = 2, version = 2L })
        };

        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var snapshot));
        Assert.Equal("2", snapshot!.Values["count"].Value);
        Assert.Equal(2, snapshot.Values["count"].Version);
        Assert.Equal(["write", "read"], snapshot.Operations.Select(operation => operation.Kind));
    }

    [Fact]
    public void Playback_rejects_a_read_that_disagrees_with_the_recorded_version()
    {
        var events = new[]
        {
            Event(1, ExecutionEventKinds.VariableWritten, "set", new { variableId = "message", dataType = "string", previousValue = "old", value = "new", version = 2L }),
            Event(2, ExecutionEventKinds.VariableRead, "get", new { variableId = "message", dataType = "string", value = "stale", version = 2L })
        };

        Assert.False(VariableEvidence.TryReconstructPlayback(events, out _));
    }

    [Fact]
    public void Playback_ignores_unrelated_events_but_rejects_malformed_variable_evidence()
    {
        var unrelated = new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.NodeCompleted, "node", Guid.NewGuid(), Data: "{}");
        Assert.False(VariableEvidence.TryRead(unrelated, out _));

        var malformed = unrelated with { Kind = ExecutionEventKinds.VariableRead, Data = "{\"variableId\":\"x\"}" };
        Assert.False(VariableEvidence.TryRead(malformed, out _));
    }

    private static ExecutionEvent Event(long sequence, string kind, string nodeId, object data) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sequence, DateTimeOffset.UtcNow, kind, nodeId, Guid.NewGuid(),
            Data: JsonSerializer.Serialize(data));
}
