using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioProjectWorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-projects-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProjectStore_CreatesDeterministicDefaultAndArchivesWithoutDeleting()
    {
        var store = new StudioProjectStore(directory);
        var defaultProject = Assert.Single(store.List());
        var created = store.Create("Research");

        Assert.Equal(StudioProjectStore.DefaultProjectId, defaultProject.Id);
        Assert.Equal("Research", store.GetActive(created.Id).Name);
        Assert.True(store.SetArchived(created.Id, true));
        Assert.Contains(store.List(), project => project.Id == created.Id && project.IsArchived);
        Assert.Throws<KeyNotFoundException>(() => store.GetActive(created.Id));
        Assert.True(store.SetArchived(created.Id, false));
        Assert.Equal("Research", store.GetActive(created.Id).Name);
    }

    [Fact]
    public void FolderProjectRegistration_IsIdempotentAndDoesNotCreateCatalogStorage()
    {
        Directory.CreateDirectory(directory);
        var folder = Path.Combine(directory, "source");
        Directory.CreateDirectory(folder);
        var store = new StudioProjectStore(Path.Combine(directory, "studio"));

        var registered = store.RegisterFolderSource(folder, "Research");
        var duplicate = store.RegisterFolderSource(Path.Combine(folder, "."), "Different display name");
        var reopenedStore = new StudioProjectStore(Path.Combine(directory, "studio"));

        Assert.True(registered.IsFolderBacked);
        Assert.Equal(registered.Id, duplicate.Id);
        Assert.Equal(Path.GetFullPath(folder), reopenedStore.FolderProjectPath(registered.Id));
        Assert.Throws<InvalidOperationException>(() => reopenedStore.ProjectDirectory(registered.Id));
        Assert.True(reopenedStore.SetArchived(registered.Id, true));
        var reopenedFolder = reopenedStore.RegisterFolderSource(folder, "Research");
        Assert.False(reopenedFolder.IsArchived);
        Assert.False(Directory.Exists(Path.Combine(directory, "studio", "projects", registered.Id)));
    }

    [Fact]
    public async Task FirstWorkspaceOpen_MigratesLegacyWorkflowAndSkillCatalogsToDefaultProject()
    {
        Directory.CreateDirectory(directory);
        var oldCatalog = new SqliteWorkflowCatalog(Path.Combine(directory, "workflows.db"), "migration-test");
        var definition = WorkflowTemplates.Create("uppercase-reverse");
        var layout = WorkflowLayout.Create(definition);
        var saved = await ((IWorkflowDesignCatalog)oldCatalog).SaveDesignAsync(definition, layout, 0);
        var published = await oldCatalog.PublishAsync(definition.WorkflowId, saved.Draft.Generation);
        var publishedDesign = await oldCatalog.GetDesignAsync(definition.WorkflowId);
        var audit = await oldCatalog.ReadAuditAsync(definition.WorkflowId);
        var oldSkills = new StudioSkillLibrary(directory);
        oldSkills.Add("research", "1.0.0", "Research", "Gather sources.", "Planning");

        var projectStore = new StudioProjectStore(directory);
        var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "runs")), NullLogger<LocalWorkflowRuntime>.Instance);
        var workspace = new StudioProjectWorkspace(directory, projectStore, runtime);
        var migrated = Assert.IsAssignableFrom<IWorkflowDesignCatalog>(workspace.Catalog);

        var migratedDraft = await migrated.GetDesignAsync(definition.WorkflowId);
        var migratedRevision = await workspace.Catalog.GetRevisionAsync(published.Revision.RevisionId);
        var migratedAudit = await workspace.Catalog.ReadAuditAsync(definition.WorkflowId);
        var migratedLayout = await migrated.GetRevisionLayoutAsync(published.Revision.RevisionId);

        Assert.Equal(WorkflowJson.SerializeDraft(publishedDesign!.Draft.Definition), WorkflowJson.SerializeDraft(migratedDraft!.Draft.Definition));
        Assert.Equal(publishedDesign.Draft.Generation, migratedDraft.Draft.Generation);
        Assert.Equal(publishedDesign.Draft.HeadRevisionId, migratedDraft.Draft.HeadRevisionId);
        Assert.Equal(publishedDesign.Draft.ActiveRevisionId, migratedDraft.Draft.ActiveRevisionId);
        Assert.Equal(published.Version, migratedRevision!.Version);
        Assert.Equal(published.Revision.RevisionId, migratedRevision.Revision.RevisionId);
        Assert.Equal(published.Revision.ContentHash, migratedRevision.Revision.ContentHash);
        Assert.Equal(published.Revision.Author, migratedRevision.Revision.Author);
        Assert.Equal(published.Revision.CreatedAt, migratedRevision.Revision.CreatedAt);
        Assert.Equal(audit, migratedAudit);
        Assert.Equal(WorkflowLayoutJson.Serialize(layout, definition), WorkflowLayoutJson.Serialize(migratedDraft.Layout, definition));
        Assert.Equal(WorkflowLayoutJson.Serialize(layout, definition), WorkflowLayoutJson.Serialize(migratedLayout!, definition));
        Assert.Contains(workspace.Skills.List(), skill => skill.Skill.Id == "research" && skill.Folder == "Planning");

        var otherProject = projectStore.Create("Experiments");
        workspace.Select(otherProject.Id);
        Assert.Empty(await workspace.Catalog.ListDraftsAsync());
        Assert.Empty(workspace.Skills.List());
        Assert.Null(await workspace.Catalog.GetRevisionAsync(published.Revision.RevisionId));
        Assert.True(File.Exists(Path.Combine(directory, "workflows.db")), "Legacy source data is retained after migration.");
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
