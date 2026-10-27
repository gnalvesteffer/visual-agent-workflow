using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Blazor;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class DirectRunLayoutTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-direct-layout-" + Guid.NewGuid());

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DirectRunCapturesMatchingSavedLayoutWithoutPublishing(bool paused, bool parameterized, bool sqlite)
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        layout = layout with { Nodes = layout.Nodes.SetItem("step-1", new(new(912, 376), "Normalize input", "Saved comment")) };
        var catalog = new SqliteWorkflowCatalog(Path.Combine(directory, "workflows.db"));
        var saved = await catalog.SaveDesignAsync(definition, layout, 0);
        var journal = Store(sqlite);
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var client = new PublishedWorkflowClient(catalog, runtime);
            var started = await ((paused, parameterized) switch
            {
                (false, false) => client.StartRunAsync(definition, "abc"),
                (false, true) => client.StartRunWithParametersAsync(definition, "abc", null),
                (true, false) => client.StartPausedRunAsync(definition, "abc"),
                (true, true) => client.StartPausedRunWithParametersAsync(definition, "abc", null)
            });
            Assert.Equal(layout.Nodes["step-1"], started.Header.Layout?.Nodes["step-1"]);
            await catalog.SaveDesignAsync(definition, WorkflowLayout.Create(definition), saved.Draft.Generation);
            var recorded = Store(sqlite).Get(started.Header.RunId);
            Assert.Equal(WorkflowLayoutJson.Serialize(layout, definition), WorkflowLayoutJson.Serialize(recorded!.Header.Layout!, definition));
            Assert.Null(recorded.Header.SourceProvenance);
            Assert.Empty(await catalog.ListRevisionsAsync(definition.WorkflowId));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DifferentDefinitionWithSameIdDoesNotBorrowSavedPresentation()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var catalog = new SqliteWorkflowCatalog(Path.Combine(directory, "workflows.db"));
        await catalog.SaveDesignAsync(definition, WorkflowLayout.Create(definition), 0);
        var journal = new FileRunJournal(Path.Combine(directory, "runs"));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var client = new PublishedWorkflowClient(catalog, runtime);
            var different = WorkflowTemplates.Create("reverse") with { WorkflowId = definition.WorkflowId };
            var run = await client.StartRunAsync(different, "abc");
            Assert.Null(run.Header.Layout);
            Assert.Equal(WorkflowJson.ComputeHash(different), run.Header.ContentHash);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "foreign")]
    [InlineData(false, "coordinate")]
    [InlineData(false, "schema")]
    [InlineData(true, "missing")]
    [InlineData(true, "foreign")]
    [InlineData(true, "coordinate")]
    [InlineData(true, "schema")]
    public void InvalidLayoutIsRejectedBeforePersistingARun(bool sqlite, string invalidity)
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        var invalid = invalidity switch
        {
            "missing" => layout with { Nodes = layout.Nodes.Remove("step-1") },
            "foreign" => layout with { Nodes = layout.Nodes.Add("foreign", new(new(0, 0))) },
            "coordinate" => layout with { Nodes = layout.Nodes.SetItem("step-1", new(new(-1, 0))) },
            _ => layout with { SchemaVersion = "unsupported" }
        };
        var revision = WorkflowRevision.Create(definition, "test") with { Layout = invalid };
        var store = Store(sqlite);
        Assert.Throws<InvalidDataException>(() => store.Create(revision, "abc"));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedPresentationIsRejectedBeforePersistingARun(bool sqlite)
    {
        var definition = WorkflowTemplates.Create("uppercase");
        for (var index = 0; index < 100; index++)
            definition = WorkflowEditing.InsertAfter(definition, "input", NodeTypes.Uppercase);
        var layout = WorkflowLayout.Create(definition);
        layout = layout with { Nodes = layout.Nodes.ToImmutableSortedDictionary(pair => pair.Key,
            pair => pair.Value with { Comment = new string('界', 4000) }, StringComparer.Ordinal) };
        WorkflowLayoutJson.Validate(layout, definition);
        var revision = WorkflowRevision.Create(definition, "test") with { Layout = layout };
        var store = Store(sqlite);
        Assert.Throws<InvalidDataException>(() => store.Create(revision, "abc"));
        Assert.Empty(store.List());
    }

    private IWorkflowRunStore Store(bool sqlite) => sqlite
        ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.db"))
        : new FileRunJournal(Path.Combine(directory, "runs"));

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
