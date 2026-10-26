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

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
