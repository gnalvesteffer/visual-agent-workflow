using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Framework-only spike for the bounded-abandon review (Decision 0070, 2026-11-24): what pinned Agent
/// Framework 1.26.0 does with a run whose executor is running and ignores its cancellation token.
/// No VAW runner code is involved.
/// </summary>
public sealed class FrameworkRunAbandonmentTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private sealed class Probe
    {
        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HeldFinished;
    }

    private sealed class StartExecutor() : Executor<string, string>("start", declareCrossRunShareable: true)
    {
        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(message);
    }

    /// <summary>Holds until released and deliberately never observes its cancellation token.</summary>
    private sealed class TokenIgnoringExecutor(Probe probe) : Executor<string, string>("held", declareCrossRunShareable: true)
    {
        public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            probe.HeldStarted.TrySetResult();
            await probe.Release.Task;
            probe.HeldFinished = true;
            return message;
        }
    }

    private static Workflow Build(Probe probe)
    {
        var start = new StartExecutor();
        var held = new TokenIgnoringExecutor(probe);
        var builder = new WorkflowBuilder(start);
        builder.AddEdge<string>((ExecutorBinding)start, (ExecutorBinding)held, _ => true);
        builder.WithOutputFrom(held);
        return builder.Build();
    }

    private static async Task<bool> ReturnsWithin(Func<Task> action, TimeSpan window)
    {
        var task = Task.Run(action);
        var winner = await Task.WhenAny(task, Task.Delay(window));
        if (winner == task) { await task; return true; }
        return false;
    }

    [Fact]
    public async Task Cancel_and_dispose_block_until_a_token_ignoring_executor_returns()
    {
        var probe = new Probe();
        var run = await InProcessExecution.Concurrent.RunStreamingAsync(Build(probe), "start");
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync()) { }
        });
        await probe.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelTask = Task.Run(() => run.CancelRunAsync().AsTask());
        var disposeTask = Task.Run(() => run.DisposeAsync().AsTask());

        // Observed with Agent Framework 1.26.0: neither teardown call returns while the executor is running.
        Assert.False(await ReturnsWithin(() => cancelTask, Window));
        Assert.False(await ReturnsWithin(() => disposeTask, Window));
        Assert.False(probe.HeldFinished);

        probe.Release.TrySetResult();
        Assert.True(await ReturnsWithin(() => cancelTask, TimeSpan.FromSeconds(10)));
        Assert.True(await ReturnsWithin(() => disposeTask, TimeSpan.FromSeconds(10)));
        Assert.True(await ReturnsWithin(() => watch, TimeSpan.FromSeconds(10)));
        Assert.True(probe.HeldFinished);
    }

    [Fact]
    public async Task Event_observation_can_be_abandoned_by_token_while_the_executor_keeps_running()
    {
        var probe = new Probe();
        var run = await InProcessExecution.Concurrent.RunStreamingAsync(Build(probe), "start");
        using var cancellation = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            await foreach (var _ in run.WatchStreamAsync(cancellation.Token)) { }
        });
        await probe.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        // Cancelling the watch token ends observation (it faults with cancellation) without stopping the run.
        Assert.True(await ReturnsWithin(async () =>
        {
            try { await watch; } catch (OperationCanceledException) { }
        }, Window));
        Assert.False(probe.HeldFinished);

        probe.Release.TrySetResult();
        Assert.True(await ReturnsWithin(() => run.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10)));
        Assert.True(probe.HeldFinished);
    }
}
