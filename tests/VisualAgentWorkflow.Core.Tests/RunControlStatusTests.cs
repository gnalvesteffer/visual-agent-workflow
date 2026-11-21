using System.Collections.Immutable;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class RunControlStatusTests
{
    [Fact]
    public void NodeSteppingIsLimitedToSequentialGraphBoundaries()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var linear = new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "debug-test", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
        Assert.True(WorkflowDebugging.SupportsNodeStepping(linear));
        Assert.True(WorkflowDebugging.SupportsBreakpoint(linear, "input"));
        Assert.True(WorkflowDebugging.SupportsBreakpoint(linear, "output"));

        foreach (var unsupported in new[]
        {
            NodeTypes.Condition, NodeTypes.BooleanBranch, NodeTypes.Merge,
            NodeTypes.ParallelFork, NodeTypes.ParallelBranch, NodeTypes.ParallelJoin,
            NodeTypes.ParallelRendezvous, NodeTypes.Loop, NodeTypes.LoopBody,
            NodeTypes.Sequence, NodeTypes.WorkflowCall, NodeTypes.WorkflowInterfaceCall
        })
        {
            var definition = linear with { Nodes = linear.Nodes.Add("unsupported", new(unsupported, "1.0.0")) };
            Assert.False(WorkflowDebugging.SupportsNodeStepping(definition));
        }
    }

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
