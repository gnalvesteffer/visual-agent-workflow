using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SequenceProductionRunnerTests
{
    [Fact]
    public async Task Production_runner_consumes_a_persisted_sequence_definition()
    {
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(PersistedSequenceDefinition(), "public-sequence");

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null,
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("hello", result.Output);
        Assert.Equal(["inventory", "architecture", "quality"], events
            .Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched)
            .Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched), eventItem =>
            Assert.Equal("sequence", ReadEvidence(eventItem).SequenceNodeId));
    }

    [Fact]
    public async Task Production_runner_records_ordered_sequence_lifecycle_through_normal_recorder()
    {
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(runId, "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.False(string.IsNullOrEmpty(result.Output));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceStarted));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted));
        var dispatched = events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched).ToArray();
        Assert.Equal([0, 1, 2], dispatched.Select(ReadEvidence).Select(evidence => evidence.Ordinal));
        Assert.Equal(["first", "second", "third"], dispatched.Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => SequenceEvidence.IsLifecycleEvent(eventItem.Kind)), eventItem =>
        {
            Assert.True(SequenceEvidence.TryRead(eventItem, out var evidence));
            Assert.NotNull(evidence);
            Assert.Equal(runId, eventItem.RunId);
        });
        Assert.Equal(Enumerable.Range(1, events.Count).Select(index => (long)index), events.Select(eventItem => eventItem.Sequence));
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.Transfer &&
            eventItem.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(["first", "second", "third"], playback.Outputs.Select(output => output.OutputId));
    }

    [Theory]
    [InlineData("Failed", ExecutionEventKinds.SequenceFailed, ExecutionEventKinds.SequenceOutputFailed)]
    [InlineData("Cancelled", ExecutionEventKinds.SequenceCancelled, ExecutionEventKinds.SequenceOutputCancelled)]
    public async Task Production_runner_preserves_distinct_sequence_failure_outcomes(
        string terminalStateName, string sequenceTerminalKind, string outputTerminalKind)
    {
        var events = new List<ExecutionEvent>();
        var terminalState = Enum.Parse<SequenceTerminalState>(terminalStateName);
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, "second", terminalState);

        Assert.True(result.Failed);
        Assert.Contains(events, eventItem => eventItem.Kind == outputTerminalKind);
        Assert.Contains(events, eventItem => eventItem.Kind == sequenceTerminalKind);
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Production_runner_cancellation_at_dispatch_boundary_does_not_admit_next_output()
    {
        var events = new List<ExecutionEvent>();
        var gate = new BoundaryGate("sequence:second");
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate,
            cancellationToken: cancellation.Token);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched &&
            ReadEvidence(eventItem).OutputId is "second" or "third");
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    [Fact]
    public async Task Production_runner_pause_at_dispatch_boundary_releases_once_without_redispatch()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence:second");
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        gate.Release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Failed);
        Assert.Equal(3, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    private static SequenceLifecycleEvidence ReadEvidence(ExecutionEvent executionEvent)
    {
        Assert.True(SequenceEvidence.TryRead(executionEvent, out var evidence));
        return evidence!;
    }

    private static WorkflowDefinition PersistedSequenceDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([
                    new("inventory", "Inventory"), new("architecture", "Architecture"), new("quality", "Quality")])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var edges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("output", "execute"), "execution"))
            .Add("sequence-quality", new(new("sequence", "then.quality"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceGraph, "sequence-runtime", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = edges
        };
    }

    private sealed class BoundaryGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(WaitAsync(cancellationToken));
        }

        private static async Task WaitAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ReleaseGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(Release.Task.WaitAsync(cancellationToken));
        }
    }
}
