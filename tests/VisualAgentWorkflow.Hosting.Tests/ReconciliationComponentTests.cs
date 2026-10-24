using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ReconciliationComponentTests
{
    [Fact]
    public async Task CursorMismatchNeverDisplaysARecoveryClaim()
    {
        var run = Recorded("current");
        var observer = new Observer(run, () => Task.FromResult<RunReconciliation?>(Report(run, "Must not show") with { LastSequence = 2 }));
        var html = await Render(observer);
        Assert.Contains("matching evidence cursor", html);
        Assert.DoesNotContain("Must not show", html);
    }

    [Fact]
    public async Task AssessmentEmbedsWithoutExecutionServicesOrMutationControls()
    {
        var run = Recorded("current");
        var html = await Render(new Observer(run, () => Task.FromResult<RunReconciliation?>(Report(run, "<script>explanation</script>"))));
        Assert.Contains("&lt;script&gt;explanation&lt;/script&gt;", html);
        Assert.Contains("independent of the playback position", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("Resume selected run", html);
        Assert.DoesNotContain("Recover run", html);
    }

    [Fact]
    public async Task SourceReplacementIgnoresLateAssessmentEvenWhenCancellationIsIgnored()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<RunReconciliation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Recorded("old");
        var current = Recorded("current");
        WorkflowObservationPanel? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowObservationPanel>>(Parameters(
            ("ChildParameters", new Dictionary<string, object?> { [nameof(WorkflowObservationPanel.Observer)] = new Observer(old, () => pending.Task) }),
            ("Capture", (Action<WorkflowObservationPanel>)(value => component = value)))));
        await renderer.Dispatcher.InvokeAsync(() => component!.SetParametersAsync(Parameters((nameof(WorkflowObservationPanel.Observer),
            new Observer(current, () => Task.FromResult<RunReconciliation?>(Report(current, "Current assessment")))))));
        pending.SetResult(Report(old, "Stale recovery claim"));
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Current assessment", html);
        Assert.DoesNotContain("Stale recovery claim", html);
    }

    private static async Task<string> Render(IWorkflowRunObserver observer)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<WorkflowObservationPanel>(
            Parameters((nameof(WorkflowObservationPanel.Observer), observer)))).ToHtmlString());
    }
    private static ParameterView Parameters(params (string Key, object? Value)[] values) => ParameterView.FromDictionary(values.ToDictionary(v => v.Key, v => v.Value));
    private static RunSnapshot Recorded(string workflow)
    {
        var revision = WorkflowRevision.Create(PipelineTemplates.Create("uppercase") with { WorkflowId = workflow }, "test");
        var id = Guid.NewGuid();
        return new(new(id, revision.RevisionId, null, "test", revision.CreatedAt, revision.ContentHash, revision.Definition, "private"),
            [new(id, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Completed)]);
    }
    private static RunReconciliation Report(RunSnapshot run, string reason) => new(run.Header.RunId, run.Header.RevisionId, run.Header.ContentHash,
        run.Events.Length, run.Status, RunRecoveryDisposition.Terminal, false, reason, null, [], 0, false);
    private sealed class Observer(RunSnapshot run, Func<Task<RunReconciliation?>> assessment) : IWorkflowRunObserver, IWorkflowRunReconciliationObserver
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunSummary>>([RunSummary.From(run)]);
        public Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult<RunSnapshot?>(run);
        public Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunReconciliation?> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default) => assessment();
    }
}
