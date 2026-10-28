using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Non-public design proof for the deferred Blueprint-style Sequence feature.
/// This documents Agent Framework dispatch behavior without adding a workflow schema.
/// </summary>
public sealed class SequenceDispatchProofTests
{
    [Fact]
    public async Task Runner_owned_sequence_proof_gates_each_output_and_records_lineage()
    {
        var result = await SequenceRunnerProof.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(["first", "second", "third"], result.DispatchOrder);
        Assert.Equal(result.DispatchOrder, result.CompletionOrder);
        Assert.Equal(3, result.Events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId is not null && evt.NodeId.StartsWith("output:", StringComparison.Ordinal)));
        Assert.Equal(1, result.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceStarted));
        Assert.Equal(1, result.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceCompleted));

        var dispatched = result.Events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched).ToArray();
        Assert.Equal(3, dispatched.Length);
        Assert.Equal([0, 1, 2], dispatched.Select(evt => SequenceEvidence.TryRead(evt, out var evidence) ? evidence!.Ordinal : -1));
        Assert.Equal(["first", "second", "third"], dispatched.Select(evt => SequenceEvidence.TryRead(evt, out var evidence) ? evidence!.OutputId : null));
        Assert.All(dispatched, evt => Assert.NotNull(evt.ConnectionId));
        Assert.DoesNotContain(result.Events, evt => evt.Kind is ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled);
    }

    [Theory]
    [InlineData("Failed", "sequence.output.failed", "sequence.failed")]
    [InlineData("Cancelled", "sequence.output.cancelled", "sequence.cancelled")]
    public async Task Runner_owned_sequence_proof_skips_outputs_after_terminal_failure_or_cancellation(
        string terminalStateName, string outputTerminalKind, string sequenceTerminalKind)
    {
        var terminalState = Enum.Parse<SequenceTerminalState>(terminalStateName);
        var result = await SequenceRunnerProof.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), "second", terminalState);

        Assert.Equal(["first", "second"], result.DispatchOrder);
        Assert.Equal(result.DispatchOrder, result.CompletionOrder);
        Assert.DoesNotContain("third", result.DispatchOrder);
        Assert.Contains(result.Events, evt => evt.Kind == outputTerminalKind && evt.NodeId == "sequence-proof");
        Assert.Contains(result.Events, evt => evt.Kind == sequenceTerminalKind);
        Assert.Equal(["third"], result.Events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped)
            .Select(evt => SequenceEvidence.TryRead(evt, out var evidence) ? evidence!.OutputId : null));
        Assert.DoesNotContain(result.Events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Direct_dispatch_can_admit_the_next_output_while_the_first_is_still_active()
    {
        var fixture = new DispatchFixture();
        var workflow = fixture.Build();
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, "start");
        var watch = Task.Run(async () =>
        {
            await foreach (var evt in run.WatchStreamAsync())
                if (evt is WorkflowErrorEvent error)
                    throw new InvalidOperationException("Sequence dispatch proof workflow failed.", error.Exception);
        });

        try
        {
            await fixture.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(fixture.FirstCompleted.Task.IsCompleted,
                "The first downstream executor should still be active when the second dispatch is observed.");
            Assert.Equal(["first", "second"], fixture.DispatchOrder.ToArray());
            Assert.Equal(2, fixture.StartOrder.Count);
            Assert.Contains("first", fixture.StartOrder);
            Assert.Contains("second", fixture.StartOrder);

            fixture.ReleaseFirst.TrySetResult();
            await fixture.AllCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            fixture.ReleaseFirst.TrySetResult();
            if (!watch.IsCompleted) await run.CancelRunAsync();
        }

        Assert.Equal(["second", "first"], fixture.CompletionOrder.ToArray());
    }

    [Fact]
    public async Task Cancellation_between_dispatches_prevents_later_output_admission()
    {
        var fixture = new DispatchFixture(holdBeforeSecondDispatch: true);
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(fixture.Build(), "start");
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync()) { }
        });

        await fixture.SecondDispatchReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first"], fixture.DispatchOrder.ToArray());
        Assert.DoesNotContain("second", fixture.StartOrder);

        await run.CancelRunAsync();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first"], fixture.DispatchOrder.ToArray());
        Assert.DoesNotContain("second", fixture.StartOrder);
    }

    private sealed class DispatchFixture(bool holdBeforeSecondDispatch = false)
    {
        private readonly TaskCompletionSource firstStarted = NewSignal();
        private readonly TaskCompletionSource secondStarted = NewSignal();
        private readonly TaskCompletionSource firstCompleted = NewSignal();
        private readonly TaskCompletionSource allCompleted = NewSignal();
        private readonly TaskCompletionSource releaseFirst = NewSignal();
        private readonly TaskCompletionSource secondDispatchReady = NewSignal();
        private readonly ConcurrentQueue<string> dispatchOrder = new();
        private readonly ConcurrentQueue<string> startOrder = new();
        private readonly ConcurrentQueue<string> completionOrder = new();
        private int completedCount;

        public bool HoldBeforeSecondDispatch { get; } = holdBeforeSecondDispatch;

        public TaskCompletionSource FirstStarted => firstStarted;
        public TaskCompletionSource SecondStarted => secondStarted;
        public TaskCompletionSource FirstCompleted => firstCompleted;
        public TaskCompletionSource AllCompleted => allCompleted;
        public TaskCompletionSource ReleaseFirst => releaseFirst;
        public TaskCompletionSource SecondDispatchReady => secondDispatchReady;
        public TaskCompletionSource ReleaseSecondDispatch { get; } = NewSignal();
        public IReadOnlyCollection<string> DispatchOrder => dispatchOrder.ToArray();
        public IReadOnlyCollection<string> StartOrder => startOrder.ToArray();
        public IReadOnlyCollection<string> CompletionOrder => completionOrder.ToArray();

        public void RecordDispatch(string output) => dispatchOrder.Enqueue(output);
        public void RecordStart(string output) => startOrder.Enqueue(output);
        public bool RecordCompletion(string output)
        {
            completionOrder.Enqueue(output);
            return Interlocked.Increment(ref completedCount) == 2;
        }

        public Workflow Build()
        {
            var dispatch = new DispatchExecutor(this);
            var first = new ProbeExecutor("first", this);
            var second = new ProbeExecutor("second", this);
            var complete = new CompletionExecutor(this);
            var builder = new WorkflowBuilder(dispatch);

            // These edges make the targets valid framework destinations. The dispatch
            // executor sends the two messages explicitly, in declared order.
            builder.AddEdge<DispatchMessage>((ExecutorBinding)dispatch, (ExecutorBinding)first, message => message?.Output == "first");
            builder.AddEdge<DispatchMessage>((ExecutorBinding)dispatch, (ExecutorBinding)second, message => message?.Output == "second");
            builder.AddEdge<ProbeResult>((ExecutorBinding)first, (ExecutorBinding)complete, _ => true);
            builder.AddEdge<ProbeResult>((ExecutorBinding)second, (ExecutorBinding)complete, _ => true);
            builder.WithOutputFrom(complete);
            return builder.Build();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DispatchExecutor : Executor<string, DispatchMessage>
    {
        private readonly DispatchFixture fixture;

        public DispatchExecutor(DispatchFixture fixture) : base("dispatch", declareCrossRunShareable: true) => this.fixture = fixture;

        public override async ValueTask<DispatchMessage> HandleAsync(string message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            fixture.RecordDispatch("first");
            await context.SendMessageAsync(new DispatchMessage("first"), "first", cancellationToken);
            fixture.SecondDispatchReady.TrySetResult();
            if (fixture.HoldBeforeSecondDispatch)
                await fixture.ReleaseSecondDispatch.Task.WaitAsync(cancellationToken);
            fixture.RecordDispatch("second");
            await context.SendMessageAsync(new DispatchMessage("second"), "second", cancellationToken);
            return new DispatchMessage("dispatched");
        }
    }

    private sealed class ProbeExecutor : Executor<DispatchMessage, ProbeResult>
    {
        private readonly string output;
        private readonly DispatchFixture fixture;

        public ProbeExecutor(string output, DispatchFixture fixture) : base(output, declareCrossRunShareable: true)
        {
            this.output = output;
            this.fixture = fixture;
        }

        public override async ValueTask<ProbeResult> HandleAsync(DispatchMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            fixture.RecordStart(output);
            if (output == "first")
            {
                fixture.FirstStarted.TrySetResult();
                await fixture.ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else fixture.SecondStarted.TrySetResult();

            if (output == "first") fixture.FirstCompleted.TrySetResult();
            if (fixture.RecordCompletion(output)) fixture.AllCompleted.TrySetResult();
            return new ProbeResult(output);
        }
    }

    private sealed class CompletionExecutor(DispatchFixture fixture) : Executor<ProbeResult, string>("complete", declareCrossRunShareable: true)
    {
        private readonly ConcurrentDictionary<string, byte> results = new(StringComparer.Ordinal);

        public override async ValueTask<string> HandleAsync(ProbeResult message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            results.TryAdd(message.Output, 0);
            await fixture.AllCompleted.Task.WaitAsync(cancellationToken);
            return string.Join(',', results.Keys.Order(StringComparer.Ordinal));
        }
    }

    private sealed record DispatchMessage(string Output);
    private sealed record ProbeResult(string Output);
}
