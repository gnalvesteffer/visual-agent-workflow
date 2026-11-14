using System.Collections.Immutable;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class RunControlStatusTests
{
    [Fact]
    public void OutcomeUnknownIsTerminalAndKeepsItsDistinctStatus()
    {
        var header = new RunHeader(Guid.NewGuid(), Guid.NewGuid(), null, "test", DateTimeOffset.UtcNow, "", null!, "");
        var events = new[] { ExecutionEventKinds.Started, ExecutionEventKinds.OutcomeUnknown }
            .Select((kind, index) => new ExecutionEvent(header.RunId, header.RevisionId, index + 1,
                DateTimeOffset.UtcNow, kind)).ToImmutableArray();

        var snapshot = new RunSnapshot(header, events);

        Assert.Equal("outcome-unknown", snapshot.Status);
        Assert.True(snapshot.IsTerminal);
    }

    [Theory]
    [InlineData("run.pause-requested", "node.completed", "pausing")]
    [InlineData("run.pause-requested", "model.responded", "pausing")]
    [InlineData("run.paused", "run.started", "paused")]
    [InlineData("run.paused", "run.completed", "completed")]
    [InlineData("run.paused", "run.interrupted", "interrupted")]
    [InlineData("run.paused", "run.cancelled", "cancelled")]
    [InlineData("run.pause-requested", "run.resumed", "queued")]
    public void RecordedControlStateSurvivesEvidenceUntilATransition(string first, string second, string expected)
    {
        var header = new RunHeader(Guid.NewGuid(), Guid.NewGuid(), null, "test", DateTimeOffset.UtcNow, "", null!, "");
        var events = new[] { first, second }.Select((kind, index) => new ExecutionEvent(header.RunId, header.RevisionId,
            index + 1, DateTimeOffset.UtcNow, kind)).ToImmutableArray();
        Assert.Equal(expected, new RunSnapshot(header, events).Status);
    }
}
