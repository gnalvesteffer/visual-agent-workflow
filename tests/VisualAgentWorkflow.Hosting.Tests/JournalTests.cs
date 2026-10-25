using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-tests-" + Guid.NewGuid());

    [Fact]
    public void RecordedEvidenceSurvivesReopeningWithoutAnExecutor()
    {
        var journal = new FileRunJournal(directory);
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test");
        var run = journal.Create(revision, "hello");
        journal.Append(new(run.Header.RunId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Started));
        journal.Append(new(run.Header.RunId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.Completed, Data: "olleh"));
        var reopened = new FileRunJournal(directory).Get(run.Header.RunId)!;
        Assert.Equal("completed", reopened.Status);
        Assert.Equal("olleh", reopened.Events[^1].Data);
        Assert.Equal(revision.ContentHash, reopened.Header.ContentHash);
        Assert.Single(journal.List());
        Assert.Throws<InvalidOperationException>(() => journal.Append(reopened.Events[^1] with { Sequence = 3 }));
    }

    [Fact]
    public void SequenceAndRevisionMismatchCannotBeAppended()
    {
        var journal = new FileRunJournal(directory);
        var run = journal.Create(WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "test"), "hello");
        var evt = new ExecutionEvent(run.Header.RunId, run.Header.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.Started);
        Assert.Throws<InvalidOperationException>(() => journal.Append(evt));
        Assert.Throws<InvalidOperationException>(() => journal.Append(evt with { Sequence = 1, RevisionId = Guid.NewGuid() }));
        Assert.Empty(journal.Get(run.Header.RunId)!.Events);
    }

    [Fact]
    public void RestartMarksUnfinishedRunWithoutReexecuting()
    {
        var journal = new FileRunJournal(directory);
        var run = journal.Create(WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "test"), "hello");
        var reopened = new FileRunJournal(directory);
        reopened.MarkUnfinishedAsInterrupted();
        reopened.MarkUnfinishedAsInterrupted();
        var result = reopened.Get(run.Header.RunId)!;
        Assert.Equal("interrupted", result.Status);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task RecordedViewerRendersWithNoRuntimeOrClientRegistered()
    {
        var journal = new FileRunJournal(directory);
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test");
        var run = journal.Create(revision, "<script>unsafe</script>");
        journal.Append(new(run.Header.RunId, revision.RevisionId, 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.NodeCompleted, "output", Guid.NewGuid(), Data: "<script>unsafe</script>"));
        var snapshot = journal.Get(run.Header.RunId)!;
        // Only rendering services: playback must not resolve a runtime, client, model, or tool.
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });
        Assert.Contains("&lt;script&gt;unsafe&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>unsafe</script>", html);
        Assert.Contains("Recorded events", html);
    }

    [Fact]
    public async Task BackgroundRuntimeCompletesAndHistoryRemainsAfterShutdown()
    {
        var journal = new FileRunJournal(directory);
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartRunAsync(WorkflowTemplates.Create("uppercase-reverse"), "hello");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunSnapshot result;
            do
            {
                await Task.Delay(10, timeout.Token);
                result = (await runtime.GetRunAsync(run.Header.RunId))!;
            } while (!result.IsTerminal);
            Assert.Equal("completed", result.Status);
            Assert.Equal("OLLEH", result.Events[^1].Data);
            await runtime.StopAsync(CancellationToken.None);
            var recorded = new FileRunJournal(directory).Get(run.Header.RunId)!;
            Assert.Equal(result.Events.ToArray(), recorded.Events.ToArray());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ParameterOverridesAreExecutedAndPersistedWithTheRun()
    {
        var definition = new WorkflowDefinition("1.6", "parameter-service", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["value"] = new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "answer" },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("input", "out"), new("value", "in"), "data"),
                ["b"] = new(new("value", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("answer", new("Answer", "default"))
        };
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var queued = await runtime.StartRunWithParametersAsync(definition, "ignored", new Dictionary<string, string> { ["answer"] = "override" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunSnapshot result;
            do
            {
                await Task.Delay(10, timeout.Token);
                result = (await runtime.GetRunAsync(queued.Header.RunId))!;
            } while (!result.IsTerminal);

            Assert.Equal("override", result.Events[^1].Data);
            Assert.Equal("override", result.Header.Parameters!["answer"]);
            Assert.Equal("override", new FileRunJournal(directory).Get(queued.Header.RunId)!.Header.Parameters!["answer"]);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
