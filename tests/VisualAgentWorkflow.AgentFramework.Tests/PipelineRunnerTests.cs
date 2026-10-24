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
    public async Task ExpandedPublishedPipelineExecutesWithoutSourceCatalogOrRuntimePrefabLookup()
    {
        var source = Revision();
        var composition = PipelineReuse.Insert(source.Definition, WorkflowLayout.Create(source.Definition), "input",
            new(1, source), WorkflowLayout.Create(source.Definition));
        var revision = WorkflowRevision.Create(composition.Definition, "consumer");
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), revision, "abc", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("ABC", output); // uppercase/reverse from the copy, followed by uppercase/reverse from the target.
        Assert.Equal(6, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(5, events.Count(e => e.Kind == ExecutionEventKinds.Transfer));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
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
    [Fact]
    public async Task CompletionBoundaryWaitsWithoutRepeatingFinalNode()
    {
        var gate = new CompletionGate();
        var events = new List<ExecutionEvent>();
        var execution = new PipelineRunner().ExecuteControlledAsync(Guid.NewGuid(), Revision(), "abc",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.Completed);
        gate.Release.TrySetResult();
        Assert.Equal("CBA", await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    private sealed class CompletionGate : IWorkflowExecutionGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId is not null) return;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

}
