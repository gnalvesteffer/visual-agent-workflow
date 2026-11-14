using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class BlueprintParityPlaybackProofTests
{
    [Fact]
    public async Task Recorded_authored_route_is_rendered_passively_without_a_runtime()
    {
        var definition = ConditionalDefinition();
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "playback-proof");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(runId, revision, "say yes", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        Assert.Equal("SAY YES", result.Output);

        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "playback-proof", DateTimeOffset.UtcNow,
            revision.ContentHash, revision.Definition, "say yes"), events.ToImmutableArray());
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Recorded events", html, StringComparison.Ordinal);
        Assert.Contains("Selected output: true", html, StringComparison.Ordinal);
        Assert.Contains("SAY YES", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tool_outcome_is_explained_without_suggesting_a_blind_retry()
    {
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "unknown-outcome-playback");
        var runId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Started),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow,
                ExecutionEventKinds.ToolAuthorized, "tool", Data: "{\"toolCallId\":\"call-1\"}"),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow,
                ExecutionEventKinds.ToolUncertain, "tool", Data: "{\"toolCallId\":\"call-1\"}"),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow,
                ExecutionEventKinds.OutcomeUnknown));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "unknown-outcome-playback",
            DateTimeOffset.UtcNow, revision.ContentHash, revision.Definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Equal("outcome-unknown", snapshot.Status);
        Assert.True(snapshot.IsTerminal);
        Assert.Contains("Latest run status: outcome-unknown", html, StringComparison.Ordinal);
        Assert.Contains("A tool outcome is unknown.", html, StringComparison.Ordinal);
        Assert.Contains("Review its recorded events before running again", html, StringComparison.Ordinal);
        Assert.Contains("operation may already have taken effect", html, StringComparison.Ordinal);
        Assert.DoesNotContain("start a new run to retry", html, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowDefinition ConditionalDefinition()
    {
        var baseline = WorkflowTemplates.Create("uppercase");
        var graph = GraphEditing.InsertConditionalAfter(baseline, "input", "out");
        var condition = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        graph = GraphEditing.InsertNodeAfter(graph, condition, "true", new(NodeTypes.Uppercase, "1.0.0"));
        return GraphEditing.InsertNodeAfter(graph, condition, "false", new(NodeTypes.Reverse, "1.0.0"));
    }
}
