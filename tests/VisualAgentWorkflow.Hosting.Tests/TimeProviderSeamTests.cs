using VisualAgentWorkflow.Blazor;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>Decision 0081 prerequisite 2: host run timing reads the injected TimeProvider.</summary>
public sealed class TimeProviderSeamTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-clock-" + Guid.NewGuid());

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
    public async Task RuntimeRecordsEveryEventWithTheInjectedClockAndAdvancesOnlyWhenTold()
    {
        var clock = new ManualTimeProvider(Start);
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory),
            NullLogger<LocalWorkflowRuntime>.Instance, null, null, null, null, clock);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var paused = await runtime.StartPausedRunAsync(WorkflowTemplates.Create("uppercase-reverse"), "abc");
            clock.Advance(TimeSpan.FromMinutes(7));
            await runtime.ResumeRunAsync(paused.Header.RunId);
            var finished = await WaitFor(runtime, paused.Header.RunId, run => run.IsTerminal);

            Assert.Equal("completed", finished.Status);
            Assert.Equal(Start, finished.Events.Single(evt => evt.Kind == ExecutionEventKinds.PauseRequested).Timestamp);
            Assert.Equal(Start.AddMinutes(7), finished.Events.Single(evt => evt.Kind == ExecutionEventKinds.Resumed).Timestamp);
            Assert.Equal(Start.AddMinutes(7), finished.Events[^1].Timestamp);
            Assert.All(finished.Events, evt => Assert.Contains(evt.Timestamp, new[] { Start, Start.AddMinutes(7) }));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ApprovalRequestTimestampComesFromTheInjectedClock()
    {
        var clock = new ManualTimeProvider(Start);
        var definition = GraphEditing.InsertApprovalBefore(WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"),
            "step-1", new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64)))), "step-1");
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory),
            NullLogger<LocalWorkflowRuntime>.Instance, new UnusedBroker(), null, null, null, clock);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            clock.Advance(TimeSpan.FromHours(1));
            var started = await runtime.StartRunAsync(definition, "abc");
            var waiting = await WaitFor(runtime, started.Header.RunId, run => run.Status == "waiting-approval");

            var request = WorkflowApprovalEvidence.Pending(waiting)!;
            Assert.Equal(Start.AddHours(1), request.RequestedAt);
            Assert.Equal(Start.AddHours(1), waiting.Events.Single(evt => evt.Kind == ExecutionEventKinds.ApprovalRequested).Timestamp);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void RestartInterruptionUsesTheInjectedClock()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new FileRunJournal(directory);
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "test"), "hello");

        store.MarkUnfinishedAsInterrupted(clock);

        Assert.Equal(Start, store.Get(run.Header.RunId)!.Events.Single().Timestamp);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class UnusedBroker : INodeToolBroker
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);
        public INodeToolScope OpenScope(NodeInvocationContext invocation) => throw new NotSupportedException();
    }
}
