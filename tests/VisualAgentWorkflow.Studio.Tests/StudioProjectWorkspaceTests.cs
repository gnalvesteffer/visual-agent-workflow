using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Text.Json;
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
    public async Task OfflineCutover_PreservesProjectIdentityAndPublishesValidatedSourceFolder()
    {
        Directory.CreateDirectory(directory);
        var store = new StudioProjectStore(Path.Combine(directory, "studio"));
        var project = store.Create("Cutover project");
        var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "studio", "runs")),
            NullLogger<LocalWorkflowRuntime>.Instance);
        var workspace = new StudioProjectWorkspace(Path.Combine(directory, "studio"), store, runtime);
        workspace.Select(project.Id);
        var definition = WorkflowTemplates.Create("uppercase-reverse") with { WorkflowId = "cutover-workflow" };
        var layout = WorkflowLayout.Create(definition);
        await Assert.IsAssignableFrom<IWorkflowDesignCatalog>(workspace.Catalog).SaveDesignAsync(definition, layout, 0);
        workspace.Skills.Add("cutover-skill", "1.0.0", "Cutover skill", "Review source.", "Review");

        var destination = Path.Combine(directory, "portable-project");
        var migrated = await StudioProjectMigration.MigrateToFolderAsync(Path.Combine(directory, "studio"), project.Id, destination);
        var reopenedStore = new StudioProjectStore(Path.Combine(directory, "studio"));
        var (manifest, files) = StudioProjectSourceDirectory.Read(destination);

        Assert.Equal(project.Id, migrated.Id);
        Assert.Equal(project.Id, manifest.ProjectId);
        Assert.Equal(Path.GetFullPath(destination), reopenedStore.FolderProjectPath(project.Id));
        Assert.Contains(manifest.Files, item => item.Kind == "workflow" && item.Id == definition.WorkflowId);
        Assert.Contains(manifest.Files, item => item.Kind == "skill" && item.Id == "cutover-skill");
        Assert.Contains(files.Keys, path => path.StartsWith("workflows/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory, "studio", "projects", project.Id, "workflows.db")),
            "The original catalog remains available as local recovery data.");
        Assert.False(File.Exists(Path.Combine(directory, "studio", "project-cutover.json")));
    }

    [Fact]
    public async Task Workspace_NavigationCanOpenWhenDefaultProjectIsFolderBacked()
    {
        var dataDirectory = Path.Combine(directory, "studio");
        var folder = Path.Combine(directory, "folder-project");
        Directory.CreateDirectory(directory);
        _ = new StudioProjectStore(dataDirectory);
        _ = await StudioProjectMigration.MigrateToFolderAsync(dataDirectory, StudioProjectStore.DefaultProjectId, folder);

        var workspace = new StudioProjectWorkspace(dataDirectory, new StudioProjectStore(dataDirectory),
            new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "runs")), NullLogger<LocalWorkflowRuntime>.Instance));

        Assert.Equal(StudioProjectStore.DefaultProjectId, workspace.CurrentProjectId);
        Assert.Equal("Default Project", workspace.CurrentProjectName);
        Assert.Throws<InvalidOperationException>(() => _ = workspace.Catalog);
    }

    [Fact]
    public async Task OfflineCutover_RejectsFolderAlreadyRegisteredToAnotherProjectBeforePublishing()
    {
        var dataDirectory = Path.Combine(directory, "studio");
        var destination = Path.Combine(directory, "existing-folder-registration");
        Directory.CreateDirectory(directory);
        var store = new StudioProjectStore(dataDirectory);
        _ = store.RegisterFolderSource(destination, "Existing folder project");

        await Assert.ThrowsAsync<InvalidOperationException>(() => StudioProjectMigration.MigrateToFolderAsync(
            dataDirectory, StudioProjectStore.DefaultProjectId, destination));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(dataDirectory, "project-cutover.json")));
    }

    [Fact]
    public void StudioHostLock_PreventsConcurrentHostOrMaintenanceProcess()
    {
        Directory.CreateDirectory(directory);
        using var first = StudioProjectMigration.AcquireHostLock(directory);
        Assert.Throws<InvalidOperationException>(() => StudioProjectMigration.AcquireHostLock(directory));
        first.Dispose();
        using var next = StudioProjectMigration.AcquireHostLock(directory);
    }

    [Fact]
    public void CutoverRecovery_CompletesValidatedStagingBeforeWorkspaceStartup()
    {
        var dataDirectory = Path.Combine(directory, "studio");
        var store = new StudioProjectStore(dataDirectory);
        var project = store.Create("Recovery project");
        var destination = Path.Combine(directory, "recovered-project");
        var staging = Path.Combine(directory, ".vaw-cutover-recovery");
        var manifest = new StudioProjectManifest(StudioProjectSourceArchive.FormatVersion, project.Id, project.Name,
            ImmutableArray<StudioProjectFile>.Empty);
        StudioProjectSourceDirectory.Write(staging, manifest, new Dictionary<string, byte[]>());
        var manifestHash = StudioProjectSourceArchive.ComputeManifestHash(manifest);
        File.WriteAllText(Path.Combine(dataDirectory, "project-cutover.json"), JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            destination,
            stagingDirectory = staging,
            manifestHash
        }));

        StudioProjectMigration.Recover(dataDirectory, store);

        Assert.Equal(Path.GetFullPath(destination), new StudioProjectStore(dataDirectory).FolderProjectPath(project.Id));
        Assert.True(Directory.Exists(destination));
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(Path.Combine(dataDirectory, "project-cutover.json")));
    }

    [Fact]
    public void CutoverRecovery_RejectsMalformedJournalBeforeTouchingProjectMapping()
    {
        var dataDirectory = Path.Combine(directory, "studio");
        var store = new StudioProjectStore(dataDirectory);
        var project = store.Create("Journal project");
        File.WriteAllText(Path.Combine(dataDirectory, "project-cutover.json"),
            $$"""{"projectId":"{{project.Id}}","destination":null,"stagingDirectory":null,"manifestHash":null}""");

        Assert.Throws<InvalidDataException>(() => StudioProjectMigration.Recover(dataDirectory, store));
        Assert.False(store.GetActive(project.Id).IsFolderBacked);
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
