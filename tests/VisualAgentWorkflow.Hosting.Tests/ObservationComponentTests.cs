using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ObservationComponentTests
{
    [Fact]
    public async Task ConsumerCanEmbedReadOnlyGraphWithoutStudioOrRuntimeServices()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var run = RecordedRun("consumer-run");
        var observer = new Observer(() => Task.FromResult<IReadOnlyList<RunSummary>>([RunSummary.From(run)]), _ => Task.FromResult<RunSnapshot?>(run));
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<WorkflowObservationPanel>(Parameters(
                (nameof(WorkflowObservationPanel.Observer), observer), (nameof(WorkflowObservationPanel.SourceLabel), "<script>source</script>")));
            return root.ToHtmlString();
        });
        Assert.Contains("consumer-run", html);
        Assert.Contains("&lt;script&gt;source&lt;/script&gt;", html);
        Assert.Contains("Workflow graph canvas", html);
        Assert.Contains("State at recorded position: completed", html);
        Assert.DoesNotContain("Start independent run", html);
        Assert.DoesNotContain("Cancel selected run", html);
        Assert.DoesNotContain("Save draft", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task RebindingIgnoresAnOldSourceEvenWhenItsProviderIgnoresCancellation()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<RunSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = RecordedRun("old-source");
        var current = RecordedRun("current-source");
        var oldObserver = new Observer(() => pending.Task, _ => Task.FromResult<RunSnapshot?>(old));
        var newObserver = new Observer(() => Task.FromResult<IReadOnlyList<RunSummary>>([RunSummary.From(current)]), _ => Task.FromResult<RunSnapshot?>(current));
        WorkflowObservationPanel? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowObservationPanel>>(Parameters(
            ("ChildParameters", new Dictionary<string, object?> { [nameof(WorkflowObservationPanel.Observer)] = oldObserver }),
            ("Capture", (Action<WorkflowObservationPanel>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(Parameters((nameof(WorkflowObservationPanel.Observer), newObserver))));
        pending.SetResult([RunSummary.From(old)]);
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("current-source", html);
        Assert.DoesNotContain("old-source", html);
    }

    [Fact]
    public async Task DisposalReleasesObservationWaitsWithoutDependingOnBackendCancellationSupport()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<RunSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer(() => pending.Task, _ => throw new InvalidOperationException("Must not load a run."));
        WorkflowObservationPanel? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowObservationPanel>>(Parameters(
            ("ChildParameters", new Dictionary<string, object?> { [nameof(WorkflowObservationPanel.Observer)] = observer }),
            ("Capture", (Action<WorkflowObservationPanel>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.Task.IsCompleted);
        await renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask());
    }

    private static ParameterView Parameters(params (string Key, object? Value)[] values) => ParameterView.FromDictionary(values.ToDictionary(v => v.Key, v => v.Value));
    private static RunSnapshot RecordedRun(string workflow)
    {
        var definition = WorkflowTemplates.Create("uppercase") with { WorkflowId = workflow };
        var revision = WorkflowRevision.Create(definition, "fixture");
        var id = Guid.NewGuid();
        return new(new(id, revision.RevisionId, null, revision.Author, revision.CreatedAt, revision.ContentHash, definition, "abc"),
            [new(id, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Completed, Data: "ABC")]);
    }
    private sealed class Observer(Func<Task<IReadOnlyList<RunSummary>>> list, Func<Guid, Task<RunSnapshot?>> get) : IWorkflowRunObserver
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default) => list();
        public Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => get(runId);
        public Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
