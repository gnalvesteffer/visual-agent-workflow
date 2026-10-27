using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-catalog-" + Guid.NewGuid());
    private SqliteWorkflowCatalog Open() => new(Path.Combine(directory, "workflows.db"), "test-actor");

    [Fact]
    public async Task PublishRestoreAndActivatePreserveImmutableHistoryAcrossReopen()
    {
        var catalog = Open();
        var firstDraft = await catalog.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        var first = await catalog.PublishAsync(firstDraft.Definition.WorkflowId, firstDraft.Generation);
        var current = (await catalog.GetDraftAsync(firstDraft.Definition.WorkflowId))!;
        var secondDraft = await catalog.SaveDraftAsync(WorkflowTemplates.Create("reverse"), current.Generation);
        var second = await catalog.PublishAsync(secondDraft.Definition.WorkflowId, secondDraft.Generation);
        current = (await catalog.GetDraftAsync(firstDraft.Definition.WorkflowId))!;
        var restored = await catalog.RestoreDraftAsync(current.Definition.WorkflowId, first.Revision.RevisionId, current.Generation);
        Assert.Equal(second.Revision.RevisionId, restored.ActiveRevisionId);
        Assert.Equal(first.Revision.ContentHash, WorkflowJson.ComputeHash(restored.Definition));
        var third = await catalog.PublishAsync(restored.Definition.WorkflowId, restored.Generation);
        Assert.Equal(3, third.Version);
        Assert.Equal(second.Revision.RevisionId, third.Revision.ParentRevisionId);
        Assert.Equal(first.Revision.ContentHash, third.Revision.ContentHash);
        var reopened = Open();
        current = (await reopened.GetDraftAsync(restored.Definition.WorkflowId))!;
        var activated = await reopened.ActivateAsync(current.Definition.WorkflowId, first.Revision.RevisionId, current.Generation);
        Assert.Equal(first.Revision.RevisionId, activated.ActiveRevisionId);
        Assert.Equal(third.Revision.RevisionId, activated.HeadRevisionId);
        var history = await reopened.ListRevisionsAsync(current.Definition.WorkflowId);
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(v => v.Version));
        Assert.Equal(second.Revision.ContentHash, history[1].Revision.ContentHash);
        Assert.Equal(first.Revision.CreatedAt, history[2].Revision.CreatedAt);
        Assert.Equal("test-actor", history[2].Revision.Author);
    }

    [Fact]
    public async Task StaleSavePublishRestoreAndActivateLeaveNoMutationOrAudit()
    {
        var catalog = Open();
        var definition = WorkflowTemplates.Create("uppercase");
        var saved = await catalog.SaveDraftAsync(definition, 0);
        var revision = await catalog.PublishAsync(definition.WorkflowId, saved.Generation);
        var before = (await catalog.GetDraftAsync(definition.WorkflowId))!;
        var audit = await catalog.ReadAuditAsync(definition.WorkflowId);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => catalog.SaveDraftAsync(WorkflowTemplates.Create("reverse"), 0));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => catalog.PublishAsync(definition.WorkflowId, 0));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => catalog.RestoreDraftAsync(definition.WorkflowId, revision.Revision.RevisionId, 0));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => catalog.ActivateAsync(definition.WorkflowId, revision.Revision.RevisionId, 0));
        var after = (await catalog.GetDraftAsync(definition.WorkflowId))!;
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(WorkflowJson.ComputeHash(before.Definition), WorkflowJson.ComputeHash(after.Definition));
        Assert.Equal(audit.ToArray(), (await catalog.ReadAuditAsync(definition.WorkflowId)).ToArray());
        Assert.Single(await catalog.ListRevisionsAsync(definition.WorkflowId));
    }

    [Fact]
    public async Task DeletedWorkflowCanBeRestoredWithoutLosingRevisionsOrRunAudit()
    {
        var catalog = Open();
        var draft = await catalog.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        var published = await catalog.PublishAsync(draft.Definition.WorkflowId, draft.Generation);
        var beforeDelete = (await catalog.GetDraftAsync(draft.Definition.WorkflowId))!;

        await catalog.ArchiveWorkflowAsync(draft.Definition.WorkflowId, beforeDelete.Generation);

        Assert.Empty(await catalog.ListDraftsAsync());
        var archived = Assert.Single(await catalog.ListArchivedDraftsAsync());
        Assert.Equal(beforeDelete.Generation + 1, archived.Generation);
        Assert.Single(await catalog.ListRevisionsAsync(draft.Definition.WorkflowId));
        var auditAfterDelete = await catalog.ReadAuditAsync(draft.Definition.WorkflowId);
        Assert.Equal(published.Revision.RevisionId, Assert.Single(auditAfterDelete,
            entry => entry.Kind == "revision.published-and-activated").RevisionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveDraftAsync(archived.Definition, archived.Generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.PublishAsync(archived.Definition.WorkflowId, archived.Generation));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => catalog.RestoreArchivedWorkflowAsync(archived.Definition.WorkflowId, beforeDelete.Generation));

        await catalog.RestoreArchivedWorkflowAsync(archived.Definition.WorkflowId, archived.Generation);

        var restored = Assert.Single(await catalog.ListDraftsAsync());
        Assert.Equal(archived.Generation + 1, restored.Generation);
        Assert.Empty(await catalog.ListArchivedDraftsAsync());
        Assert.Single(await catalog.ListRevisionsAsync(draft.Definition.WorkflowId));
        Assert.Contains(await catalog.ReadAuditAsync(draft.Definition.WorkflowId), entry => entry.Kind == "workflow.archived");
        Assert.Contains(await catalog.ReadAuditAsync(draft.Definition.WorkflowId), entry => entry.Kind == "workflow.restored");
    }

    [Fact]
    public async Task ConcurrentStoreInstancesCannotOverwriteSameGeneration()
    {
        var first = Open();
        var second = Open();
        var saved = await first.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        using var gate = new ManualResetEventSlim();
        async Task<bool> Save(SqliteWorkflowCatalog store, string operation)
        {
            gate.Wait();
            try { await store.SaveDraftAsync(WorkflowTemplates.Create(operation), saved.Generation); return true; }
            catch (WorkflowConflictException) { return false; }
        }
        var a = Task.Run(() => Save(first, "reverse"));
        var b = Task.Run(() => Save(second, "uppercase-reverse"));
        gate.Set();
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, result => result);
        Assert.Equal(2, (await first.GetDraftAsync(saved.Definition.WorkflowId))!.Generation);
        Assert.Equal(2, (await first.ReadAuditAsync(saved.Definition.WorkflowId)).Count);
    }

    [Fact]
    public async Task CrossWorkflowRestoreAndActivationAreRejectedWithoutAudit()
    {
        var catalog = Open();
        var original = await catalog.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        var other = await catalog.SaveDraftAsync(WorkflowTemplates.Create("reverse") with { WorkflowId = "other" }, 0);
        var published = await catalog.PublishAsync("other", other.Generation);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RestoreDraftAsync(original.Definition.WorkflowId, published.Revision.RevisionId, original.Generation));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ActivateAsync(original.Definition.WorkflowId, published.Revision.RevisionId, original.Generation));
        Assert.Single(await catalog.ReadAuditAsync(original.Definition.WorkflowId));
    }

    [Fact]
    public async Task PublishedRunUsesStoredIdentityEvenAfterDraftChanges()
    {
        var catalog = Open();
        var saved = await catalog.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        var published = await catalog.PublishAsync(saved.Definition.WorkflowId, saved.Generation);
        var draft = (await catalog.GetDraftAsync(saved.Definition.WorkflowId))!;
        await catalog.SaveDraftAsync(WorkflowTemplates.Create("reverse"), draft.Generation);
        var journal = new FileRunJournal(Path.Combine(directory, "runs"));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var client = new PublishedWorkflowClient(catalog, runtime);
            var started = await client.StartPublishedRunAsync(published.Revision.RevisionId, "abc");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunSnapshot recorded;
            do
            {
                await Task.Delay(10, timeout.Token);
                recorded = (await client.GetRunAsync(started.Header.RunId))!;
            } while (!recorded.IsTerminal);
            Assert.Equal(published.Revision.RevisionId, recorded.Header.RevisionId);
            Assert.Equal(published.Revision.ContentHash, recorded.Header.ContentHash);
            Assert.Equal("ABC", recorded.Events[^1].Data);
            Assert.Equal("completed", recorded.Status);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AuditIsOrderedAttributedAndTransactionallyConsistent()
    {
        var catalog = Open();
        var draft = await catalog.SaveDraftAsync(WorkflowTemplates.Create("uppercase"), 0);
        var published = await catalog.PublishAsync(draft.Definition.WorkflowId, draft.Generation);
        var entries = await Open().ReadAuditAsync(draft.Definition.WorkflowId);
        Assert.Equal(new[] { "draft.saved", "revision.published-and-activated" }, entries.Select(e => e.Kind));
        Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.Generation));
        Assert.All(entries, entry => Assert.Equal("test-actor", entry.Actor));
        Assert.Equal(published.Revision.RevisionId, entries[1].RevisionId);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
