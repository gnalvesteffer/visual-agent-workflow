using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>Decision 0081 viewer minimum: a recorded wait renders a readable state from its own evidence, never the clock.</summary>
public sealed class WaitViewerTests
{
    private static readonly DateTimeOffset Recorded = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static WorkflowDefinition Definition() => new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-view", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("wait1", new(NodeTypes.Wait, "1.0.0") { Wait = new(90_000) })
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("v", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("a", new(new("input", "then"), new("wait1", "execute"), "execution"))
            .Add("b", new(new("wait1", "then"), new("output", "execute"), "execution"))
    };

    private static async Task<string> Render(params string[] kinds)
    {
        var definition = Definition();
        var revision = WorkflowRevision.Create(definition, "test");
        var header = new RunHeader(Guid.NewGuid(), revision.RevisionId, null, "test", Recorded, revision.ContentHash, definition, "in");
        var invocation = Guid.NewGuid();
        var waitId = Guid.NewGuid();
        var events = ImmutableArray.CreateBuilder<ExecutionEvent>();
        events.Add(new(header.RunId, header.RevisionId, 1, Recorded, ExecutionEventKinds.Started));
        events.Add(new(header.RunId, header.RevisionId, 2, Recorded, ExecutionEventKinds.NodeStarted, "wait1", invocation));
        foreach (var kind in kinds)
            events.Add(new(header.RunId, header.RevisionId, events.Count + 1, Recorded, kind, "wait1", invocation, Data: kind == ExecutionEventKinds.WaitRequested
                ? JsonSerializer.Serialize(new { waitId, durationMilliseconds = 90_000, deadlineUtc = Recorded.AddSeconds(90) })
                : JsonSerializer.Serialize(new { waitId })));
        var snapshot = new RunSnapshot(header, events.ToImmutable());
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });
    }

    [Fact]
    public async Task A_pending_wait_shows_the_recorded_duration_and_deadline_not_a_clock_derived_countdown()
    {
        var html = await Render(ExecutionEventKinds.WaitRequested);

        Assert.Contains("Waiting 00:01:30", html, StringComparison.Ordinal);
        Assert.Contains("recorded deadline 2031-05-06 07:09:39 UTC (display only)", html, StringComparison.Ordinal);
        Assert.Contains(">Waiting<", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionEventKinds.WaitElapsed, "Wait elapsed")]
    [InlineData(ExecutionEventKinds.WaitCancelled, "Wait cancelled")]
    [InlineData(ExecutionEventKinds.WaitInterrupted, "Wait interrupted by host shutdown")]
    public async Task A_settled_wait_shows_its_settlement(string settlement, string label)
    {
        var html = await Render(ExecutionEventKinds.WaitRequested, settlement);

        Assert.Contains(label, html, StringComparison.Ordinal);
        Assert.DoesNotContain("recorded deadline", html, StringComparison.Ordinal);
    }
}
