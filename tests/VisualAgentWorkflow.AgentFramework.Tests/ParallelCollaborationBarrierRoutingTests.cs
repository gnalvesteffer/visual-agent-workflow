using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ParallelCollaborationBarrierRoutingTests
{
    [Fact]
    public async Task FanInRendezvous_ReleasesBranchSnapshotsWhenAgentConcurrencyIsBelowBranchCount()
    {
        var fixture = new BarrierFixture(branchCount: 3, maximumAgentConcurrency: 1);
        var workflow = fixture.Build();

        await RunAsync(workflow);

        Assert.Equal(1, fixture.MaximumActiveAgents);
        Assert.Equal(3, fixture.PostBarrierResults.Count);
        Assert.All(fixture.PostBarrierResults.Values, snapshot => Assert.Equal("branch_a,branch_b,branch_c", snapshot));
    }

    [Fact]
    public async Task FanInRendezvous_DoesNotReleaseAnyBranchWhenAPreBarrierAgentFails()
    {
        var fixture = new BarrierFixture(branchCount: 3, maximumAgentConcurrency: 1, failingBranchId: "branch_b");
        var workflow = fixture.Build();

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(workflow));

        Assert.Empty(fixture.PostBarrierResults);
    }

    [Fact]
    public async Task FanInRendezvous_CancellationBeforeAllArrivalsDoesNotReleaseAnyBranch()
    {
        var fixture = new BarrierFixture(branchCount: 3, maximumAgentConcurrency: 3, pausedBranchId: "branch_c");
        var workflow = fixture.Build();
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, "start");
        var watchedEvents = new List<WorkflowEvent>();
        var watch = Task.Run(async () =>
        {
            await foreach (var evt in run.WatchStreamAsync()) watchedEvents.Add(evt);
        });

        await fixture.PausedAgentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.OtherAgentsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.CancelRunAsync();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.PostBarrierResults);
        Assert.DoesNotContain(watchedEvents, evt => evt is WorkflowOutputEvent);
    }

    private static async Task RunAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, "start", cancellationToken: cancellationToken);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            if (evt is WorkflowErrorEvent error)
                throw new InvalidOperationException("Pinned MAF workflow failed.", error.Exception);
    }

    private sealed class BarrierFixture
    {
        private readonly SemaphoreSlim agentPermit;
        private readonly string? failingBranchId;
        private readonly string? pausedBranchId;
        private readonly TaskCompletionSource pausedAgentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource otherAgentsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> completedPreAgents = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource releasePausedAgent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, string> results = new(StringComparer.Ordinal);
        private int activeAgents;
        private int maximumActiveAgents;

        public BarrierFixture(int branchCount, int maximumAgentConcurrency, string? failingBranchId = null,
            string? pausedBranchId = null)
        {
            if (branchCount is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(branchCount));
            agentPermit = new(maximumAgentConcurrency);
            BranchIds = Enumerable.Range(0, branchCount).Select(index => $"branch_{(char)('a' + index)}").ToArray();
            this.failingBranchId = failingBranchId;
            this.pausedBranchId = pausedBranchId;
        }

        private string[] BranchIds { get; }
        public IReadOnlyDictionary<string, string> PostBarrierResults => results;
        public int MaximumActiveAgents => maximumActiveAgents;
        public TaskCompletionSource PausedAgentStarted => pausedAgentStarted;
        public TaskCompletionSource OtherAgentsCompleted => otherAgentsCompleted;

        public Workflow Build()
        {
            var start = new StartExecutor();
            var pre = BranchIds.ToDictionary(branchId => branchId, branchId =>
                new PreBarrierAgentExecutor(branchId, this), StringComparer.Ordinal);
            var barrier = new RendezvousExecutor(BranchIds);
            var post = BranchIds.ToDictionary(branchId => branchId, branchId =>
                new PostBarrierAgentExecutor(branchId, this), StringComparer.Ordinal);
            var complete = new CompletionExecutor(BranchIds.Length);
            var builder = new WorkflowBuilder(start);

            foreach (var branchId in BranchIds)
            {
                builder.AddEdge<string>(start, pre[branchId], _ => true);
            }

            builder.AddFanInBarrierEdge(pre.Values.Select(agent => (ExecutorBinding)agent), (ExecutorBinding)barrier);

            foreach (var branchId in BranchIds)
            {
                var branch = branchId;
                builder.AddEdge<BarrierMessage>((ExecutorBinding)barrier, (ExecutorBinding)post[branchId], message => message?.BranchId == branch);
            }

            builder.AddFanInBarrierEdge(post.Values.Select(agent => (ExecutorBinding)agent), (ExecutorBinding)complete);
            builder.WithOutputFrom(complete);
            return builder.Build();
        }

        private async ValueTask<string> ExecutePreBarrierAsync(string branchId, CancellationToken cancellationToken)
        {
            await agentPermit.WaitAsync(cancellationToken);
            var active = Interlocked.Increment(ref activeAgents);
            UpdateMaximum(active);
            try
            {
                if (branchId == failingBranchId) throw new InvalidOperationException("Pre-barrier model failure.");
                if (branchId == pausedBranchId)
                {
                    pausedAgentStarted.TrySetResult();
                    await releasePausedAgent.Task.WaitAsync(cancellationToken);
                }
                else if (completedPreAgents.TryAdd(branchId, 0) && completedPreAgents.Count == BranchIds.Length - 1)
                    otherAgentsCompleted.TrySetResult();
                return branchId;
            }
            finally
            {
                Interlocked.Decrement(ref activeAgents);
                agentPermit.Release();
            }
        }

        private void RecordPostBarrier(string branchId, string snapshot) => results[branchId] = snapshot;

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref maximumActiveAgents);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximumActiveAgents, active, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }

        private sealed class StartExecutor() : Executor<string, string>("start", declareCrossRunShareable: true)
        {
            public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(message);
        }

        private sealed class PreBarrierAgentExecutor(string branchId, BarrierFixture fixture)
            : Executor<string, string>($"pre-{branchId}", declareCrossRunShareable: true)
        {
            public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
                await fixture.ExecutePreBarrierAsync(branchId, cancellationToken);
        }

        private sealed class RendezvousExecutor(string[] branchIds)
            : Executor<string, BarrierMessage>("rendezvous", declareCrossRunShareable: true)
        {
            private readonly ConcurrentDictionary<string, byte> arrivals = new(StringComparer.Ordinal);

            public override async ValueTask<BarrierMessage> HandleAsync(string branchId, IWorkflowContext context, CancellationToken cancellationToken = default)
            {
                if (!arrivals.TryAdd(branchId, 0)) throw new InvalidOperationException("Duplicate branch arrival.");
                if (arrivals.Count != branchIds.Length) return new("pending", string.Empty);

                var snapshot = string.Join(',', arrivals.Keys.Order(StringComparer.Ordinal));
                foreach (var participant in branchIds)
                    await context.SendMessageAsync(new BarrierMessage(participant, snapshot), $"post-{participant}", cancellationToken);
                return new("released", snapshot);
            }
        }

        private sealed class PostBarrierAgentExecutor(string branchId, BarrierFixture fixture)
            : Executor<BarrierMessage, BarrierMessage>($"post-{branchId}", declareCrossRunShareable: true)
        {
            public override ValueTask<BarrierMessage> HandleAsync(BarrierMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
            {
                fixture.RecordPostBarrier(branchId, message.Snapshot);
                return ValueTask.FromResult(message);
            }
        }

        private sealed class CompletionExecutor(int participantCount) : Executor<BarrierMessage, string>("complete", declareCrossRunShareable: true)
        {
            private readonly ConcurrentDictionary<string, string> results = new(StringComparer.Ordinal);

            public override ValueTask<string> HandleAsync(BarrierMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
            {
                results.TryAdd(message.BranchId, message.Snapshot);
                return ValueTask.FromResult(results.Count == participantCount ? string.Join(';', results.Keys.Order(StringComparer.Ordinal)) : string.Empty);
            }
        }
    }

    private sealed record BarrierMessage(string BranchId, string Snapshot);
}
