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
    public async Task Recorded_loop_iteration_prefixes_render_the_current_activity_passively()
    {
        var definition = LoopDefinition();
        var revision = WorkflowRevision.Create(definition, "loop-playback-proof");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();

        await new WorkflowRunner().ExecuteAsync(runId, revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var iterations = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        Assert.Equal(3, iterations.Length);
        for (var index = 0; index < iterations.Length; index++)
        {
            var prefix = events.TakeWhile(evt => evt.Sequence <= iterations[index].Sequence).ToImmutableArray();
            var snapshot = Snapshot(runId, revision, prefix, "loop-playback-proof", "continue");
            var html = await Render(snapshot);

            Assert.Contains($"Iteration {index + 1} of 3", html, StringComparison.Ordinal);
            Assert.Contains($"Loop activations ({index + 1})", html, StringComparison.Ordinal);
            Assert.Contains("entering body", html, StringComparison.Ordinal);
            Assert.DoesNotContain($"Iteration {index + 2} of 3", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Recorded_parallel_arrival_prefix_renders_accepted_branch_count_passively()
    {
        var baseline = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "parallel-arrival-playback", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["data-path"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["execution-path"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        var definition = GraphEditing.InsertParallelRegionAtExecutionConnection(baseline, "execution-path");
        var revision = WorkflowRevision.Create(definition, "parallel-arrival-playback");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();

        await new WorkflowRunner().ExecuteAsync(runId, revision, "parallel input",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var firstArrival = events.First(evt => evt.Kind == ExecutionEventKinds.ParallelJoinArrival);
        var prefix = events.Where(evt => evt.Sequence <= firstArrival.Sequence).ToImmutableArray();
        Assert.True(ParallelJoinEvidence.TryReconstructPlayback(definition, prefix, out var parallelPlayback));
        Assert.Single(Assert.Single(parallelPlayback!.Activations).ReceivedBranchIds);
        var html = await Render(Snapshot(runId, revision, prefix, "parallel-arrival-playback", "parallel input"));

        Assert.Contains("Accepted 1 of 2 branch results", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted 2 of 2 branch results", html, StringComparison.Ordinal);

        var legacyEvents = events.Where(evt => evt.Kind != ExecutionEventKinds.ParallelJoinArrival).ToImmutableArray();
        var legacyHtml = await Render(Snapshot(runId, revision, legacyEvents, "parallel-arrival-playback", "parallel input"));
        Assert.Contains("Parallel arrival lineage was not recorded in this history", legacyHtml, StringComparison.Ordinal);
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

    private static WorkflowDefinition LoopDefinition()
    {
        var baseline = new WorkflowDefinition("1.0", "loop-playback-proof", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-output"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        return GraphEditing.InsertBoundedLoopAfter(baseline, "input", "out");
    }

    private static RunSnapshot Snapshot(Guid runId, WorkflowRevision revision,
        ImmutableArray<ExecutionEvent> events, string author, string input) =>
        new(new(runId, revision.RevisionId, null, author, DateTimeOffset.UtcNow,
            revision.ContentHash, revision.Definition, input), events);

    private static async Task<string> Render(RunSnapshot snapshot)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });
    }

}
