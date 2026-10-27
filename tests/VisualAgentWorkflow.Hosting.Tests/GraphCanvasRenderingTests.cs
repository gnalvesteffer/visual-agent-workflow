using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class GraphCanvasRenderingTests
{
    [Fact]
    public async Task ContextNodeLibraryPutsMcpToolsBeforeOtherActions()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        var binding = new ToolBindingDescriptor("demo.reverse", "MCP · reverse", "demo-text", "reverse",
            "a") { InputSchemaJson = "{}", ToolDescription = "Reverses text", InputSummary = "text: string required", DescriptionHash = "b" };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.Bindings)] = new[] { binding }
                }));
            return component.ToHtmlString();
        });

        var mcpIndex = html.IndexOf("data-node-key=\"mcp:demo.reverse:a:b\"", StringComparison.Ordinal);
        var valuesIndex = html.IndexOf("data-node-key=\"core.text.uppercase\"", StringComparison.Ordinal);
        Assert.True(mcpIndex >= 0);
        Assert.True(valuesIndex >= 0);
        Assert.True(mcpIndex < valuesIndex);
    }

    [Fact]
    public async Task IdeGraphMinimapIncludesViewportIndicator()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.IdeMode)] = true,
                    [nameof(WorkflowEditor.ShowWorkflowSettings)] = false
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("data-canvas-minimap", html);
        Assert.Contains("data-minimap-viewport", html);
        Assert.Contains("vaw-minimap-node", html);
    }

    [Fact]
    public async Task CommentExpansionStaysOnNodeAndDoesNotResizeInspectorEditor()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1",
                new NodePresentation(new(440, 100), Comment: "A long node comment that is expanded on the graph.", CommentExpanded: true))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.Layout)] = layout
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Collapse node comment", html);
        Assert.Contains("rows=\"6\"", html);
    }

    [Fact]
    public async Task AgentConnectionIsNotReportedUnavailableWhileProviderChoicesAreLoading()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with
        {
            EntryNodeId = "step-1",
            Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
                Agent: new("connection.current", null, "Answer the question.", [])))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var loadingHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.ProviderChoicesLoading)] = true
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Loading model connections", loadingHtml);
        Assert.DoesNotContain("Model connection unavailable: connection.current", loadingHtml);

        var completedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowEditor.Definition)] = definition }));
            return component.ToHtmlString();
        });

        Assert.Contains("Model connection unavailable: connection.current", completedHtml);
    }

    [Fact]
    public async Task RunningAgentNodeShowsRecordedUsageContextAndEstimatedCost()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":8000,"InputCostPerMillionTokens":1.5,"OutputCostPerMillionTokens":3.0,"CacheReadCostPerMillionTokens":0.2,"CacheWriteCostPerMillionTokens":0.4},"maximumTurns":8}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-1"}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","ModelId":"test-model","usage":{"InputTokenCount":1200,"OutputTokenCount":200,"TotalTokenCount":1400,"CachedInputTokenCount":200,"cacheWriteInputTokenCount":100,"ReasoningTokenCount":50}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 5, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentContextCompacted, "step-1", invocationId));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 1,200 in / 200 out", html);
        Assert.Contains("Model turns · 1 / 8", html);
        Assert.Contains("Cache · 200 read / 100 write", html);
        Assert.Contains("Reasoning · 50 tokens (included in output)", html);
        Assert.Contains("Last completed context · 1,200 / 8,000 (15%)", html);
        Assert.Contains("Host context compactions · 1", html);
        Assert.Contains("Estimated cost · $0.002030", html);
        Assert.Contains("test-model", html);
    }

    [Fact]
    public async Task RunViewerAddsNodeEventActionToEachRecordedNode()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, definition.EntryNodeId, invocationId, Data: "input"),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeCompleted, definition.EntryNodeId, invocationId, Data: "output"));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains($"View events for {definition.EntryNodeId}", html);
        Assert.Contains("vaw-node-events-button", html);
        Assert.DoesNotContain("vaw-node-events-dialog", html);
    }

    [Fact]
    public async Task AgentNodeDoesNotRenderMissingProviderUsageAsZeroes()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":128000},"maximumTurns":16}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-1"}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","ModelId":"test-model","finishReason":"stop"}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Model turns · 1 / 16", html);
        Assert.Contains("Tokens · unavailable (provider did not report usage)", html);
        Assert.Contains("Context window · 128,000 tokens; prompt usage pending", html);
        Assert.DoesNotContain("Tokens · 0 in / 0 out", html);
        Assert.DoesNotContain("Context · 0 / 0", html);
    }

    [Fact]
    public async Task AgentNodeRendersWhenProviderMetricsAreNull()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":null,"InputCostPerMillionTokens":null,"OutputCostPerMillionTokens":null}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"ModelId":"test-model","usage":{"InputTokenCount":12,"OutputTokenCount":2,"TotalTokenCount":14}}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 12 in / 2 out", html);
        Assert.Contains("Cost estimate · unavailable", html);
        Assert.Contains("test-model", html);
        Assert.Contains("Context window · not configured in this model connection", html);
        Assert.Contains("Host context compactions · 0", html);
    }

    [Fact]
    public async Task RunningAgentNodeMarksContextUsageAsStaleUntilCurrentRequestResponds()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":8000}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","usage":{"InputTokenCount":400,"OutputTokenCount":50,"TotalTokenCount":450}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-2"}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Last completed context · 400 / 8,000 (5%)", html);
        Assert.Contains("Model request in progress · current usage updates when it responds", html);
        Assert.Contains("Tokens · 400 in / 50 out", html);
    }

    [Fact]
    public async Task RecordedLayoutEncodesUntrustedLabelsAndCommentsWithoutExecutionServices()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(600, 250), "<img src=x onerror=alert(1)>", "<script>comment</script>")),
            Sections = layout.Sections.Add("section", new("<script>section</script>", ["step-1"]))
        };
        var revision = WorkflowRevision.Create(definition, "test");
        var snapshot = new RunSnapshot(new(Guid.NewGuid(), revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "hello"), []);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowRunViewer.Snapshot)] = snapshot, [nameof(WorkflowRunViewer.Layout)] = layout }));
            return component.ToHtmlString();
        });
        Assert.Contains("&lt;img", html);
        Assert.Contains("&lt;script&gt;comment&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;section&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("left:600px;top:250px", html);
        Assert.Contains("data-disabled=\"true\"", html);
    }
}
