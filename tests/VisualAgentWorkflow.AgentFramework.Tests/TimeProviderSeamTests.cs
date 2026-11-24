using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Decision 0081 prerequisite 2: run timing reads the injected TimeProvider and never the system clock.</summary>
public sealed class TimeProviderSeamTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task RunnerEventTimestampsEqualTheInjectedClockAndMoveOnlyWhenAdvanced()
    {
        var clock = new ManualTimeProvider(Start);
        var runner = new WorkflowRunner() { Clock = clock };
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase-reverse"), "fixture");

        var first = new List<ExecutionEvent>();
        await runner.ExecuteAsync(Guid.NewGuid(), revision, "abc", evt => { first.Add(evt); return ValueTask.CompletedTask; });
        clock.Advance(TimeSpan.FromHours(3));
        var second = new List<ExecutionEvent>();
        await runner.ExecuteAsync(Guid.NewGuid(), revision, "abc", evt => { second.Add(evt); return ValueTask.CompletedTask; });

        Assert.NotEmpty(first);
        Assert.All(first, evt => Assert.Equal(Start, evt.Timestamp));
        Assert.NotEmpty(second);
        Assert.All(second, evt => Assert.Equal(Start.AddHours(3), evt.Timestamp));
    }

    [Fact]
    public async Task DefaultRunnerStillUsesTheSystemClock()
    {
        var before = DateTimeOffset.UtcNow;
        var events = new List<ExecutionEvent>();
        await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "fixture"), "abc",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var after = DateTimeOffset.UtcNow;

        Assert.All(events, evt => Assert.InRange(evt.Timestamp, before, after));
    }

    [Fact]
    public async Task SequencedWriterStampsWithTheInjectedClock()
    {
        var clock = new ManualTimeProvider(Start);
        var stamps = new List<DateTimeOffset>();
        using var writer = new SequencedExecutionEventWriter(Guid.NewGuid(), Guid.NewGuid(), evt =>
        {
            stamps.Add(evt.Timestamp);
            return ValueTask.CompletedTask;
        }, new ExecutionActivationLedger(), clock);

        await writer.RecordAsync(ExecutionEventKinds.Started);
        clock.Advance(TimeSpan.FromMinutes(5));
        await writer.RecordAsync(ExecutionEventKinds.Completed);

        Assert.Equal([Start, Start.AddMinutes(5)], stamps);
    }

    [Fact]
    public void ControlledRunControlEventsUseTheInjectedClock()
    {
        var clock = new ManualTimeProvider(Start);
        var directory = Path.Combine(Path.GetTempPath(), "vaw-clock-" + Guid.NewGuid());
        try
        {
            var store = new FileRunJournal(directory);
            var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "fixture"), "abc");
            using var slot = new SemaphoreSlim(1, 1);
            using var control = new ControlledRun(store, run.Header, slot, clock);

            control.RequestPause();
            clock.Advance(TimeSpan.FromMinutes(7));
            control.Resume();

            var events = store.Get(run.Header.RunId)!.Events;
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.PauseRequested && evt.Timestamp == Start);
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Resumed && evt.Timestamp == Start.AddMinutes(7));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
