using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.AgentFramework;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class CheckpointTests
{
    [Fact]
    public async Task NativeJsonRestoresNewFrameworkGraphWithoutRerunningCompletedNodes()
    {
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase-reverse"), "test");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        ValueTask Record(ExecutionEvent evt) { events.Add(evt); return ValueTask.CompletedTask; }
        var first = new Sink(events, stopAfter: 2);
        Assert.Null(await new WorkflowRunner().ExecuteCheckpointedAsync(runId, revision, "abc 🌍", Record, first));
        Assert.Equal(new[] { "input", "step-1" }, events.Where(e => e.Kind == ExecutionEventKinds.NodeCompleted).Select(e => e.NodeId));
        Assert.DoesNotContain(events, e => e.Kind is ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Completed);
        var saved = first.Last!;
        var resumed = new Sink(events, restored: saved);
        Assert.Equal("🌍 CBA", await new WorkflowRunner().ExecuteCheckpointedAsync(runId, revision, "abc 🌍", Record, resumed));
        Assert.Equal(new[] { "input", "step-1", "step-2", "output" }, events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.NodeId));
        Assert.Single(events, e => e.Kind == ExecutionEventKinds.Recovered);
        Assert.Single(events, e => e.Kind == ExecutionEventKinds.Completed);
    }

    private sealed class Sink(List<ExecutionEvent> events, int? stopAfter = null, NativeWorkflowCheckpoint? restored = null) : IWorkflowCheckpointSink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint => restored;
        public NativeWorkflowCheckpoint? Last { get; private set; }
        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint)
        {
            Last = checkpoint;
            return ValueTask.FromResult(stopAfter is { } count && events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted) >= count);
        }
    }
}
