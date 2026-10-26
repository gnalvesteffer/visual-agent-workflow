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
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":8000,"InputCostPerMillionTokens":1.5,"OutputCostPerMillionTokens":3.0}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","ModelId":"test-model","usage":{"InputTokenCount":1200,"OutputTokenCount":200,"TotalTokenCount":1400}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentContextCompacted, "step-1", invocationId));
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
        Assert.Contains("Context · 1,200 / 8,000 (15%)", html);
        Assert.Contains("Host context compactions · 1", html);
        Assert.Contains("Estimated cost · $0.002400", html);
        Assert.Contains("test-model", html);
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
