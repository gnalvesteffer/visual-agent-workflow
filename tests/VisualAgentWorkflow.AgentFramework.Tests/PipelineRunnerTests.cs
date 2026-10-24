using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public class PipelineRunnerTests
{
    private static WorkflowRevision Revision() => WorkflowRevision.Create(new("1.0", "demo", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["uppercase"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["reverse"] = new(NodeTypes.Reverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["a"] = new(new("input", "out"), new("uppercase", "in"), "data"),
            ["b"] = new(new("uppercase", "out"), new("reverse", "in"), "data"),
            ["c"] = new(new("reverse", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary()), "test");

    [Fact]
    public async Task ExecutesUsingFrameworkAndRecordsActualTransfers()
    {
        var revision = Revision();
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner().ExecuteAsync(runId, revision, "Hello 🌍", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("🌍 OLLEH", output);
        Assert.Equal(ExecutionEventKinds.Started, events[0].Kind);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => { Assert.Equal(runId, e.RunId); Assert.Equal(revision.RevisionId, e.RevisionId); });
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(4, events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.InvocationId).Distinct().Count());
        var transfers = events.Where(e => e.Kind == ExecutionEventKinds.Transfer).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, transfers.Select(e => e.ConnectionId));
        Assert.Equal(new[] { "Hello 🌍", "HELLO 🌍", "🌍 OLLEH" }, transfers.Select(e => e.Data));
    }

    [Fact]
    public async Task CancellationRecordsTerminalOutcomeWithoutNodeExecution()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PipelineRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), "input", e => { events.Add(e); return ValueTask.CompletedTask; }, cancellation.Token));
        Assert.Equal(new[] { ExecutionEventKinds.Started, ExecutionEventKinds.Cancelled }, events.Select(e => e.Kind));
    }

    [Fact]
    public async Task InvalidDefinitionNeverStartsAFrameworkRun()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<ArgumentException>(() => new PipelineRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), new string('x', 16_385), e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.Empty(events);
    }
}
