using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>
/// Characterizes how Hosting readers and the recorded-run viewer treat an event whose Kind is not recognized
/// (decision 0081, "Unknown event kind behavior"). Unknown kinds are accepted, persisted, ignored by status and
/// reconciliation, and shown with their raw kind by the viewer. Nothing rejects them.
/// </summary>
public sealed class UnknownEventKindTests : IDisposable
{
    private const string Future = "future.unknown-kind";
    private const string Wait = "wait.requested";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-unknown-kind-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");
    private IWorkflowRunStore Store(bool sqlite) => sqlite ? new SqliteWorkflowRunStore(Database) : new FileRunJournal(directory);
    private static WorkflowRevision Revision() => WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test");
    private static ExecutionEvent Event(RunSnapshot run, long sequence, string kind, string? data = null) =>
        new(run.Header.RunId, run.Header.RevisionId, sequence, DateTimeOffset.UtcNow, kind, Data: data);

    [Theory]
    [InlineData(false, Future)]
    [InlineData(true, Future)]
    [InlineData(false, Wait)]
    [InlineData(true, Wait)]
    public void Unknown_event_kind_is_accepted_persisted_and_read_back_by_run_stores(bool sqlite, string kind)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "hello");
        var payload = JsonSerializer.Serialize(new { waitId = Guid.NewGuid(), seconds = 5 });
        var appended = store.Append(run.Header.RunId, 0, [Event(run, 1, ExecutionEventKinds.Started), Event(run, 2, kind, payload)]);
        Assert.Equal("running", appended.Status);

        var reopened = Store(sqlite).Get(run.Header.RunId)!;
        Assert.Equal(2, reopened.Events.Length);
        Assert.Equal(kind, reopened.Events[1].Kind);
        Assert.Equal(payload, reopened.Events[1].Data);
        Assert.Equal("running", reopened.Status);
        Assert.False(reopened.IsTerminal);

        var completed = Store(sqlite).Append(run.Header.RunId, 2, [Event(run, 3, ExecutionEventKinds.Completed)]);
        Assert.Equal("completed", completed.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_event_kind_after_a_terminal_outcome_is_rejected_like_any_other_kind(bool sqlite)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "hello");
        store.Append(run.Header.RunId, 0, [Event(run, 1, ExecutionEventKinds.Started), Event(run, 2, ExecutionEventKinds.Completed)]);
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 2, [Event(run, 3, Future)]));
        Assert.Equal(2, store.Get(run.Header.RunId)!.Events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blank_event_kind_is_rejected_by_run_stores_but_unknown_nonblank_kind_is_not(bool sqlite)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "hello");
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1, " ")]));
        Assert.Empty(store.Get(run.Header.RunId)!.Events);
        Assert.Single(store.Append(run.Header.RunId, 0, [Event(run, 1, "parallel.collaboration.future-kind")]).Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_event_kind_is_ignored_by_reconciliation_and_terminal_status_still_wins(bool sqlite)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "hello");
        var node = Guid.NewGuid();
        store.Append(run.Header.RunId, 0,
        [
            Event(run, 1, ExecutionEventKinds.Started),
            Event(run, 2, Wait, JsonSerializer.Serialize(new { waitId = Guid.NewGuid() })) with { NodeId = "step-1", InvocationId = node },
            Event(run, 3, Future),
            Event(run, 4, ExecutionEventKinds.Interrupted)
        ]);
        var report = (await JournalRunObserver.FromStore(store).GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal("interrupted", report.Status);
        // An unsettled unknown "requested" kind is invisible to this reader: nothing is reported as unresolved.
        Assert.Equal(RunRecoveryDisposition.Terminal, report.Disposition);
        Assert.Equal(0, report.TotalUnresolvedWork);
        Assert.False(report.CanResume);
        Assert.Equal(4, report.LastSequence);
    }

    [Fact]
    public async Task Unknown_event_kind_after_a_saved_suspension_makes_evidence_advanced_and_not_resumable()
    {
        var store = new SqliteWorkflowRunStore(Database);
        await using var host = new CheckpointWorkflowHost(store);
        var run = await host.StartAsync(Revision(), "payload", 1);
        var observer = JournalRunObserver.FromStore(store);
        Assert.Equal(RunRecoveryDisposition.SavedSuspension, (await observer.GetReconciliationAsync(run.Header.RunId))!.Disposition);
        var checkpoint = store.GetCheckpoint(run.Header.RunId)!;

        store.Append(Event(run, run.Events.Length + 1, Future));
        var advanced = (await observer.GetReconciliationAsync(run.Header.RunId))!;
        Assert.Equal(RunRecoveryDisposition.EvidenceAdvanced, advanced.Disposition);
        Assert.False(advanced.CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResumeAsync(run.Header.RunId, checkpoint.Native.CheckpointId));
    }

    [Fact]
    public async Task Unknown_event_kind_is_rendered_with_its_raw_kind_by_the_recorded_viewer()
    {
        var store = new FileRunJournal(directory);
        var run = store.Create(Revision(), "hello");
        store.Append(run.Header.RunId, 0,
        [
            Event(run, 1, ExecutionEventKinds.Started),
            Event(run, 2, Wait, "<b>raw</b>"),
            Event(run, 3, Future)
        ]);
        var snapshot = store.Get(run.Header.RunId)!;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });
        Assert.Contains("<td>wait.requested</td>", html);
        Assert.Contains("<td>future.unknown-kind</td>", html);
        Assert.Contains("Recorded events", html);
        Assert.DoesNotContain("<b>raw</b>", html);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
