using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ComponentLifecycleTests
{
    [Fact]
    public async Task StudioIgnoresOldClientResultsAfterRebinding()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<RunSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldClient = new TestClient(() => pending.Task);
        var currentRun = Run("current");
        var oldRun = Run("old");
        var currentClient = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([currentRun]));
        WorkflowStudio? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowStudio>>(Parameters(
            (nameof(CaptureHost<WorkflowStudio>.ChildParameters), new Dictionary<string, object?> { [nameof(WorkflowStudio.Client)] = oldClient }),
            (nameof(CaptureHost<WorkflowStudio>.Capture), (Action<WorkflowStudio>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(Parameters((nameof(WorkflowStudio.Client), currentClient))));
        pending.SetResult([oldRun]);
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains(currentRun.Header.RunId.ToString()[..8], html);
        Assert.DoesNotContain(oldRun.Header.RunId.ToString()[..8], html);
    }

    [Fact]
    public async Task StudioDisposalStopsWaitingWithoutCancellingBackendRuns()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<RunSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestClient(() => pending.Task);
        WorkflowStudio? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowStudio>>(Parameters(
            (nameof(CaptureHost<WorkflowStudio>.ChildParameters), new Dictionary<string, object?> { [nameof(WorkflowStudio.Client)] = client }),
            (nameof(CaptureHost<WorkflowStudio>.Capture), (Action<WorkflowStudio>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.Task.IsCompleted);
        Assert.Equal(0, client.Cancellations);
        await renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task StudioRendersWorkflowDesignerBeforeRunHistoryFinishesLoading()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pendingHistory = new TaskCompletionSource<IReadOnlyList<RunSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestClient(() => pendingHistory.Task);
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<WorkflowStudio>(Parameters(
            (nameof(WorkflowStudio.Client), client),
            (nameof(WorkflowStudio.Catalog), new TestCatalog("fast-editor")))));

        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(3));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

        Assert.Contains("Workflow designer", html);
        Assert.Contains("fast-editor", html);
        Assert.False(pendingHistory.Task.IsCompleted);
    }

    [Fact]
    public async Task DesignerShowsChoiceLoadingWithoutDiscoveringDuringStaticRender()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pendingTools = new TaskCompletionSource<IReadOnlyList<ToolBindingDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new TestCatalog("choices-pending");
        var client = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([]));
        var toolListCalls = 0;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<WorkflowDesigner>(Parameters(
            (nameof(WorkflowDesigner.Catalog), catalog),
            (nameof(WorkflowDesigner.Client), client),
            (nameof(WorkflowDesigner.ToolCatalog), new TestTools(() =>
            {
                Interlocked.Increment(ref toolListCalls);
                return pendingTools.Task;
            })))));

        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(3));
        var loadingHtml = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

        Assert.Contains("choices-pending", loadingHtml);
        Assert.Contains("Loading MCP tool choices", loadingHtml);
        Assert.False(pendingTools.Task.IsCompleted);
        Assert.Equal(0, toolListCalls);
    }

    [Fact]
    public async Task McpChoiceFailureDoesNotHideModelConnections()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowDesigner? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowDesigner>>(Parameters(
            (nameof(CaptureHost<WorkflowDesigner>.ChildParameters), new Dictionary<string, object?>
            {
                [nameof(WorkflowDesigner.Catalog)] = new TestCatalog("independent-choices"),
                [nameof(WorkflowDesigner.Client)] = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([])),
                [nameof(WorkflowDesigner.ToolCatalog)] = new TestTools(() => throw new InvalidOperationException("MCP unavailable")),
                [nameof(WorkflowDesigner.AgentCatalog)] = new TestAgents()
            }),
            (nameof(CaptureHost<WorkflowDesigner>.Capture), (Action<WorkflowDesigner>)(value => component = value)))));
        Assert.NotNull(component);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var type = typeof(WorkflowDesigner);
            var binding = type.GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
            var load = (Task)type.GetMethod("Choices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [binding])!;
            await load;
        });

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Provider that loaded", html);
        Assert.Contains("Could not load MCP tools", html);
        Assert.DoesNotContain("Could not load model connections", html);
    }

    [Fact]
    public async Task DesignerIgnoresOldCatalogAfterContextChange()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<WorkflowDraft>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCatalog = new TestCatalog("old-workspace", () => pending.Task);
        var currentCatalog = new TestCatalog("current-workspace");
        var client = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([]));
        WorkflowDesigner? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowDesigner>>(Parameters(
            (nameof(CaptureHost<WorkflowDesigner>.ChildParameters), new Dictionary<string, object?>
            { [nameof(WorkflowDesigner.Catalog)] = oldCatalog, [nameof(WorkflowDesigner.Client)] = client }),
            (nameof(CaptureHost<WorkflowDesigner>.Capture), (Action<WorkflowDesigner>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(Parameters((nameof(WorkflowDesigner.Catalog), currentCatalog))));
        pending.SetResult([oldCatalog.Draft]);
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("current-workspace", html);
        Assert.DoesNotContain("old-workspace", html);
    }

    [Fact]
    public async Task ReplacingToolCatalogDuringStaticRenderDoesNotStartDiscovery()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<ToolBindingDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new TestCatalog("preserved-workflow");
        var client = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([]));
        WorkflowDesigner? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowDesigner>>(Parameters(
            (nameof(CaptureHost<WorkflowDesigner>.ChildParameters), new Dictionary<string, object?>
            {
                [nameof(WorkflowDesigner.Catalog)] = catalog, [nameof(WorkflowDesigner.Client)] = client,
                [nameof(WorkflowDesigner.ToolCatalog)] = new TestTools(() => pending.Task)
            }),
            (nameof(CaptureHost<WorkflowDesigner>.Capture), (Action<WorkflowDesigner>)(value => component = value)))));
        Assert.NotNull(component);
        var current = new TestTools(() => Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([Tool("Current choice")]));
        await renderer.Dispatcher.InvokeAsync(() => component.SetParametersAsync(Parameters((nameof(WorkflowDesigner.ToolCatalog), current))));
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Loading MCP tool choices", html);
        Assert.False(pending.Task.IsCompleted);
        Assert.Equal("preserved-workflow", catalog.Draft.Definition.WorkflowId);
        Assert.DoesNotContain("Stale choice", html);
        Assert.Contains("preserved-workflow", html);
        Assert.Equal(2, catalog.ListCalls); // Initial selection and metadata only; discovery does not reload the draft.
    }

    [Fact]
    public async Task DesignerDisposalStopsWaitingForAnUncooperativeCatalog()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pending = new TaskCompletionSource<IReadOnlyList<ToolBindingDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestClient(() => Task.FromResult<IReadOnlyList<RunSnapshot>>([]));
        WorkflowDesigner? component = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CaptureHost<WorkflowDesigner>>(Parameters(
            (nameof(CaptureHost<WorkflowDesigner>.ChildParameters), new Dictionary<string, object?>
            {
                [nameof(WorkflowDesigner.Catalog)] = new TestCatalog("disposal"), [nameof(WorkflowDesigner.Client)] = client,
                [nameof(WorkflowDesigner.ToolCatalog)] = new TestTools(() => pending.Task)
            }),
            (nameof(CaptureHost<WorkflowDesigner>.Capture), (Action<WorkflowDesigner>)(value => component = value)))));
        Assert.NotNull(component);
        await renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.Task.IsCompleted);
        Assert.Equal(0, client.Cancellations);
    }

    [Fact]
    public async Task ErrorBoundaryShowsSafeFallbackWithoutExceptionDetails()
    {
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<WorkflowErrorBoundary>(Parameters(
            (nameof(WorkflowErrorBoundary.ChildContent), (RenderFragment)(builder => { builder.OpenComponent<ThrowingComponent>(0); builder.CloseComponent(); })),
            (nameof(WorkflowErrorBoundary.ErrorContent), (RenderFragment<Exception>)(_ => builder => builder.AddContent(0, "Safe fallback"))))));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Safe fallback", html);
        Assert.DoesNotContain("sensitive-error-detail", html);
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<IErrorBoundaryLogger, UnexpectedBoundaryLogger>()
        .AddSingleton<Microsoft.JSInterop.IJSRuntime, NoopJsRuntime>().BuildServiceProvider();
    private static ParameterView Parameters(params (string Name, object? Value)[] values) =>
        ParameterView.FromDictionary(values.ToDictionary(v => v.Name, v => v.Value));
    private static ToolBindingDescriptor Tool(string name) => new("text", name, "server", "text", new string('a', 64));
    private static RunSnapshot Run(string workflowId)
    {
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase") with { WorkflowId = workflowId }, "test");
        return new(new(Guid.NewGuid(), revision.RevisionId, null, "test", revision.CreatedAt, revision.ContentHash, revision.Definition, "input"), []);
    }

    public sealed class CaptureHost<T> : ComponentBase where T : ComponentBase
    {
        [Parameter] public IReadOnlyDictionary<string, object?> ChildParameters { get; set; } = new Dictionary<string, object?>();
        [Parameter] public Action<T>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<T>(0);
            builder.AddMultipleAttributes(1, ChildParameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((T)value));
            builder.CloseComponent();
        }
    }

    public sealed class ThrowingComponent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("sensitive-error-detail");
    }

    private sealed class UnexpectedBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) =>
            throw new InvalidOperationException("The custom boundary must use its sanitized logger override.");
    }

    private sealed class TestTools(Func<Task<IReadOnlyList<ToolBindingDescriptor>>> list) : IToolBindingCatalog
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) => list();
    }

    private sealed class TestAgents : IAgentProviderCatalog
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("provider.test", "Provider that loaded", "test-model", false)]);
    }

    private sealed class TestClient(Func<Task<IReadOnlyList<RunSnapshot>>> list) : IPublishedWorkflowClient
    {
        public int Cancellations { get; private set; }
        public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => list();
        public Task<RunSnapshot?> GetRunAsync(Guid runId) => Task.FromResult<RunSnapshot?>(null);
        public Task CancelRunAsync(Guid runId) { Cancellations++; return Task.CompletedTask; }
        public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) => throw new NotSupportedException();
        public Task<RunSnapshot> StartPublishedRunAsync(Guid revisionId, string input) => throw new NotSupportedException();
    }

    private sealed class TestCatalog : IWorkflowCatalog
    {
        public WorkflowDraft Draft { get; }
        public int ListCalls { get; private set; }
        private readonly Func<Task<IReadOnlyList<WorkflowDraft>>> list;
        public TestCatalog(string id, Func<Task<IReadOnlyList<WorkflowDraft>>>? list = null)
        {
            Draft = new(WorkflowTemplates.Create("uppercase") with { WorkflowId = id }, 1, null, null);
            this.list = list ?? (() => Task.FromResult<IReadOnlyList<WorkflowDraft>>([Draft]));
        }
        public Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync() { ListCalls++; return list(); }
        public Task<WorkflowDraft?> GetDraftAsync(string workflowId) => Task.FromResult<WorkflowDraft?>(Draft);
        public Task<IReadOnlyList<PublishedWorkflowRevision>> ListRevisionsAsync(string workflowId) => Task.FromResult<IReadOnlyList<PublishedWorkflowRevision>>([]);
        public Task<IReadOnlyList<WorkflowAuditEntry>> ReadAuditAsync(string workflowId) => Task.FromResult<IReadOnlyList<WorkflowAuditEntry>>([]);
        public Task<PublishedWorkflowRevision?> GetRevisionAsync(Guid revisionId) => Task.FromResult<PublishedWorkflowRevision?>(null);
        public Task<WorkflowDraft> SaveDraftAsync(WorkflowDefinition definition, long expectedGeneration) => throw new NotSupportedException();
        public Task<PublishedWorkflowRevision> PublishAsync(string workflowId, long expectedGeneration) => throw new NotSupportedException();
        public Task<WorkflowDraft> RestoreDraftAsync(string workflowId, Guid revisionId, long expectedGeneration) => throw new NotSupportedException();
        public Task<WorkflowDraft> ActivateAsync(string workflowId, Guid revisionId, long expectedGeneration) => throw new NotSupportedException();
    }
}
