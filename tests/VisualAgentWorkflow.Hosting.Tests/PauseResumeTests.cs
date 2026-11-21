using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class PauseResumeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-pause-" + Guid.NewGuid());
    private FileRunJournal Journal => new(directory);
    private LocalWorkflowRuntime Runtime(IAgentNodeExecutor? agents = null) => new(Journal, NullLogger<LocalWorkflowRuntime>.Instance, agents: agents);
    private static WorkflowDefinition WithAgent() => WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase-reverse"), "step-1",
        new(NodeTypes.Agent, "1.0.0", Agent: new("controlled-test", null, "", [])));

    private static async Task<RunSnapshot> WaitFor(LocalWorkflowRuntime runtime, Guid id, Func<RunSnapshot, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var snapshot = (await runtime.GetRunAsync(id))!;
            if (condition(snapshot)) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task StartPausedDoesNoNodeWorkAndDoesNotBlockAnotherRun()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var first = await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase-reverse"), "abc");
            Assert.Equal("paused", first.Status);
            Assert.DoesNotContain(first.Events, e => e.Kind == ExecutionEventKinds.NodeStarted);
            var other = await runtime.StartRunAsync(WorkflowTemplates.Create("uppercase"), "other");
            Assert.Equal("completed", (await WaitFor(runtime, other.Header.RunId, r => r.IsTerminal)).Status);
            Assert.Equal("paused", (await runtime.GetRunAsync(first.Header.RunId))!.Status);
            await runtime.PauseRunAsync(first.Header.RunId); // idempotent while paused
            await runtime.ResumeRunAsync(first.Header.RunId);
            var finished = await WaitFor(runtime, first.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Equal("CBA", finished.Events[^1].Data);
            Assert.Equal(first.Header.RunId, finished.Header.RunId);
            Assert.Equal(first.Header.RevisionId, finished.Header.RevisionId);
            Assert.Equal(first.Header.ContentHash, finished.Header.ContentHash);
            Assert.Equal(WorkflowJson.Serialize(first.Header.Definition), WorkflowJson.Serialize(finished.Header.Definition));
            Assert.Equal(4, finished.Events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.PauseRequested);
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.Paused);
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.Resumed);
            Assert.Equal(Enumerable.Range(1, finished.Events.Length).Select(i => (long)i), finished.Events.Select(e => e.Sequence));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StepAdmitsOneNodeAndPausesAtTheNextBoundary()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase-reverse"), "abc");
            await runtime.StepRunAsync(run.Header.RunId);
            var first = await WaitFor(runtime, run.Header.RunId, r => r.Status == "paused" && r.Events.Any(e => e.Kind == ExecutionEventKinds.Paused));

            Assert.Contains(first.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "input");
            Assert.DoesNotContain(first.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "step-1");
            Assert.Equal("step-1", first.Events.Last(e => e.Kind == ExecutionEventKinds.Paused).NodeId);

            await runtime.StepRunAsync(run.Header.RunId);
            var second = await WaitFor(runtime, run.Header.RunId, r => r.Status == "paused" && r.Events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted) == 2);
            Assert.Contains(second.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "step-1");
            Assert.DoesNotContain(second.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "step-2");

            await runtime.ResumeRunAsync(run.Header.RunId);
            var completed = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("CBA", completed.Events[^1].Data);
            Assert.Equal(Enumerable.Range(1, completed.Events.Length).Select(i => (long)i), completed.Events.Select(e => e.Sequence));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task BreakpointPausesBeforeNodeAndContinueSkipsOnlyThatHit()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase-reverse"), "abc");
            await runtime.SetBreakpointsAsync(run.Header.RunId, ["output"]);
            await runtime.ResumeRunAsync(run.Header.RunId);
            var paused = await WaitFor(runtime, run.Header.RunId, r => r.Status == "paused" && r.Events.Any(e => e.Kind == ExecutionEventKinds.BreakpointHit));

            Assert.DoesNotContain(paused.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "output");
            Assert.Equal("output", paused.Events.Last(e => e.Kind == ExecutionEventKinds.Paused).NodeId);
            Assert.Single(paused.Events, e => e.Kind == ExecutionEventKinds.BreakpointHit && e.NodeId == "output");

            await runtime.StepRunAsync(run.Header.RunId);
            var stepped = await WaitFor(runtime, run.Header.RunId, r => r.Status == "paused" && r.Events.Any(e => e.Kind == ExecutionEventKinds.NodeCompleted && e.NodeId == "output"));
            Assert.DoesNotContain(stepped.Events, e => e.Kind == ExecutionEventKinds.Completed);
            await runtime.ResumeRunAsync(run.Header.RunId);
            var completed = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("CBA", completed.Events[^1].Data);
            Assert.Single(completed.Events, e => e.Kind == ExecutionEventKinds.BreakpointHit);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ActiveNodeFinishesBeforePauseAndResumeDoesNotRepeatCompletedWork()
    {
        var agent = new ControlledAgent();
        using var runtime = Runtime(agent);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var started = await runtime.StartRunAsync(WithAgent(), "abc");
            var invocation = await agent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => runtime.PauseRunAsync(started.Header.RunId))));
            Assert.Equal("pausing", (await runtime.GetRunAsync(started.Header.RunId))!.Status);
            // Ongoing provider evidence must not erase the pause-requested state.
            await invocation.Record(ExecutionEventKinds.ModelResponded, "controlled provider evidence");
            Assert.Equal("pausing", (await runtime.GetRunAsync(started.Header.RunId))!.Status);
            agent.Release.TrySetResult();
            var paused = await WaitFor(runtime, started.Header.RunId, r => r.Status == "paused");
            Assert.Contains(paused.Events, e => e.NodeId == "step-1" && e.Kind == ExecutionEventKinds.NodeCompleted);
            Assert.DoesNotContain(paused.Events, e => e.NodeId == "step-2" && e.Kind == ExecutionEventKinds.NodeStarted);
            var completedInvocations = paused.Events.Where(e => e.Kind == ExecutionEventKinds.NodeCompleted).Select(e => e.InvocationId).ToArray();
            await runtime.ResumeRunAsync(started.Header.RunId);
            var finished = await WaitFor(runtime, started.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Equal("CBA", finished.Events[^1].Data);
            Assert.Equal(1, agent.Calls);
            Assert.All(completedInvocations, id => Assert.Single(finished.Events, e => e.InvocationId == id && e.Kind == ExecutionEventKinds.NodeCompleted));
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.PauseRequested);
            Assert.Equal(Enumerable.Range(1, finished.Events.Length).Select(i => (long)i), finished.Events.Select(e => e.Sequence));
            Assert.Equal(finished.Events.ToArray(), Journal.Get(started.Header.RunId)!.Events.ToArray());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ProductionParallelPauseAcknowledgementWaitsForEveryActiveAgentToSettle()
    {
        var (definition, leftAgentId, rightAgentId) = ParallelAgents();
        WorkflowValidator.ThrowIfInvalid(definition);
        var agent = new ConcurrentPauseAgent(leftAgentId, rightAgentId);
        using var runtime = Runtime(agent);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(definition, "hello");
            await Task.WhenAll(agent.LeftAgentStarted.Task, agent.RightAgentStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));

            await runtime.PauseRunAsync(run.Header.RunId);
            agent.ReleaseLeftAgent.TrySetResult();
            var leftSettled = await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.Events.Any(evt =>
                evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == leftAgentId));
            Assert.DoesNotContain(leftSettled.Events, evt => evt.Kind == ExecutionEventKinds.Paused);
            Assert.DoesNotContain(leftSettled.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == rightAgentId);

            agent.ReleaseRightAgent.TrySetResult();
            var paused = await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.Events.Any(evt =>
                evt.Kind == ExecutionEventKinds.Paused));

            Assert.Equal("paused", paused.Status);
            Assert.Contains(paused.Events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == leftAgentId);
            Assert.Contains(paused.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == leftAgentId);
            Assert.Contains(paused.Events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == rightAgentId);
            Assert.Contains(paused.Events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
                evt.NodeId == rightAgentId);
            Assert.DoesNotContain(paused.Events, evt => evt.Kind == ExecutionEventKinds.Completed);

            await runtime.ResumeRunAsync(run.Header.RunId);
            agent.ReleaseRightAgent.TrySetResult();
            var finished = await WaitFor(runtime, run.Header.RunId, snapshot => snapshot.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.Single(finished.Events, evt => evt.Kind == ExecutionEventKinds.Paused);
            Assert.Single(finished.Events, evt => evt.Kind == ExecutionEventKinds.Resumed);
        }
        finally
        {
            agent.ReleaseLeftAgent.TrySetResult();
            agent.ReleaseRightAgent.TrySetResult();
            await runtime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResumeCanWithdrawPendingPauseWithoutInterruptingTheActiveNode()
    {
        var agent = new ControlledAgent();
        using var runtime = Runtime(agent);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(WithAgent(), "abc");
            await agent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.PauseRunAsync(run.Header.RunId);
            await runtime.ResumeRunAsync(run.Header.RunId);
            await runtime.ResumeRunAsync(run.Header.RunId);
            agent.Release.TrySetResult();
            var finished = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("completed", finished.Status);
            Assert.DoesNotContain(finished.Events, e => e.Kind == ExecutionEventKinds.Paused);
            Assert.Single(finished.Events, e => e.Kind == ExecutionEventKinds.Resumed);
            Assert.Equal(1, agent.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationUnblocksPausedContinuationWithoutStartingAnAgent()
    {
        var agent = new ControlledAgent();
        using var runtime = Runtime(agent);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(WithAgent(), "abc");
            await runtime.CancelRunAsync(run.Header.RunId);
            var terminal = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("cancelled", terminal.Status);
            Assert.Equal(0, agent.Calls);
            Assert.DoesNotContain(terminal.Events, e => e.Kind == ExecutionEventKinds.NodeStarted);
            await Assert.ThrowsAsync<WorkflowRunControlException>(() => runtime.ResumeRunAsync(run.Header.RunId));
            await Assert.ThrowsAsync<WorkflowRunControlException>(() => runtime.PauseRunAsync(run.Header.RunId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => runtime.PauseRunAsync(Guid.NewGuid()));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PauseAfterLastProcessingNodeWaitsBeforeOutputWithoutRepeatingWork()
    {
        var definition = WorkflowEditing.Remove(WithAgent(), "step-2");
        var agent = new ControlledAgent();
        using var runtime = Runtime(agent);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(definition, "abc");
            await agent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.PauseRunAsync(run.Header.RunId);
            agent.Release.TrySetResult();
            var paused = await WaitFor(runtime, run.Header.RunId, r => r.Status == "paused");
            Assert.DoesNotContain(paused.Events, e => e.Kind == ExecutionEventKinds.Completed);
            await runtime.ResumeRunAsync(run.Header.RunId);
            var finished = await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
            Assert.Equal("ABC", finished.Events[^1].Data);
            Assert.Equal(1, agent.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task GracefulShutdownReleasesPausedWaitsAndRestartDoesNotResumeThem()
    {
        using (var runtime = Runtime())
        {
            await runtime.StartAsync(CancellationToken.None);
            var run = await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase"), "abc");
            await runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((await runtime.GetRunAsync(run.Header.RunId))!.IsTerminal);
            using var reopened = Runtime();
            await reopened.StartAsync(CancellationToken.None);
            try { await Assert.ThrowsAsync<WorkflowRunControlException>(() => reopened.ResumeRunAsync(run.Header.RunId)); }
            finally { await reopened.StopAsync(CancellationToken.None); }
        }
    }

    [Fact]
    public async Task CrashHistoryIsMarkedInterruptedRatherThanReexecuted()
    {
        var journal = Journal;
        var run = journal.Create(WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "test"), "abc");
        journal.Append(new(run.Header.RunId, run.Header.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Paused));
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var recovered = (await runtime.GetRunAsync(run.Header.RunId))!;
            Assert.Equal("interrupted", recovered.Status);
            Assert.DoesNotContain(recovered.Events, e => e.Kind == ExecutionEventKinds.NodeStarted);
            await Assert.ThrowsAsync<WorkflowRunControlException>(() => runtime.ResumeRunAsync(run.Header.RunId));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PausedContextsRemainWithinTheRuntimeAdmissionBudget()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var paused = new List<RunSnapshot>();
            for (var i = 0; i < 32; i++) paused.Add(await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase"), "abc"));
            var rejected = await runtime.StartRunAsync(WorkflowTemplates.Create("uppercase"), "overflow");
            Assert.Equal("failed", rejected.Status);
            foreach (var run in paused) await runtime.CancelRunAsync(run.Header.RunId);
            foreach (var run in paused) await WaitFor(runtime, run.Header.RunId, r => r.IsTerminal);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    private sealed class ControlledAgent : IAgentNodeExecutor
    {
        public TaskCompletionSource<NodeInvocationContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public int Calls => calls;
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() => Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([]);
        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult(invocation);
            await Release.Task.WaitAsync(cancellationToken);
            return input.ToUpperInvariant();
        }
    }

    private static (WorkflowDefinition Definition, string LeftAgentId, string RightAgentId) ParallelAgents()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("left", "Left", "anchor-left"),
            new ParallelBranchDescriptor("right", "Right", "anchor-right"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor-left", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "left") })
            .Add("anchor-right", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "right") })
            .Add("agent-left", new(NodeTypes.Agent, "1.0.0",
                Agent: new("controlled-parallel-pause", null, "left", [])))
            .Add("agent-right", new(NodeTypes.Agent, "1.0.0",
                Agent: new("controlled-parallel-pause", null, "right", [])))
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
                { ParallelJoin = new("fork", ["left", "right"]) })
            .Add("output", new(NodeTypes.Output, "1.0.0")
                { OutputDataType = WorkflowDataTypes.Json });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-left", new(new("input", "out"), new("agent-left", "in"), "data"))
            .Add("input-right", new(new("input", "out"), new("agent-right", "in"), "data"))
            .Add("join-output", new(new("join", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", new(new("input", "then"), new("fork", "execute"), "execution"))
            .Add("fork-left", new(new("fork", ParallelBranchLimits.PinId("left")),
                new("anchor-left", "execute"), "execution"))
            .Add("fork-right", new(new("fork", ParallelBranchLimits.PinId("right")),
                new("anchor-right", "execute"), "execution"))
            .Add("left-agent", new(new("anchor-left", "then"),
                new("agent-left", "execute"), "execution"))
            .Add("right-agent", new(new("anchor-right", "then"), new("agent-right", "execute"), "execution"))
            .Add("left-join", new(new("agent-left", "out"),
                new("join", "branch.left"), "execution"))
            .Add("right-join", new(new("agent-right", "out"),
                new("join", "branch.right"), "execution"))
            .Add("join-output", new(new("join", "then"), new("output", "execute"), "execution"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "parallel-pause-boundary", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
        return (definition, "agent-left", "agent-right");
    }

    private sealed class ConcurrentPauseAgent(string leftNodeId, string rightNodeId) : IAgentNodeExecutor
    {
        public TaskCompletionSource LeftAgentStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RightAgentStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLeftAgent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRightAgent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("controlled-parallel-pause", "Controlled parallel pause", "test", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            if (invocation.NodeId == leftNodeId)
            {
                LeftAgentStarted.TrySetResult();
                await ReleaseLeftAgent.Task.WaitAsync(cancellationToken);
            }
            else if (invocation.NodeId == rightNodeId)
            {
                RightAgentStarted.TrySetResult();
                await ReleaseRightAgent.Task.WaitAsync(cancellationToken);
            }
            return input;
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
