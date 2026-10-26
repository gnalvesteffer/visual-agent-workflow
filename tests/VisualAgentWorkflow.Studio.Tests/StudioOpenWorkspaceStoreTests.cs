using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioOpenWorkspaceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-open-workspace-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenAndSelect_PersistTabOrderAndSelectionAcrossStoreInstances()
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "studio");
        var projects = new StudioProjectStore(dataDirectory);
        var first = AddFolderProject(projects, "First");
        var second = AddFolderProject(projects, "Second");
        var store = new StudioOpenWorkspaceStore(dataDirectory, projects);

        store.Open(first.Id);
        store.Open(second.Id);
        store.Select(first.Id);
        var reopened = new StudioOpenWorkspaceStore(dataDirectory, new StudioProjectStore(dataDirectory));

        Assert.Equal([first.Id, second.Id], reopened.ProjectIds);
        Assert.Equal(first.Id, reopened.SelectedProjectId);
    }

    [Fact]
    public void CloseSelectedTab_SelectsNearestRemainingTabAndPersists()
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "studio");
        var projects = new StudioProjectStore(dataDirectory);
        var first = AddFolderProject(projects, "First");
        var second = AddFolderProject(projects, "Second");
        var third = AddFolderProject(projects, "Third");
        var store = new StudioOpenWorkspaceStore(dataDirectory, projects);
        store.Open(first.Id);
        store.Open(second.Id);
        store.Open(third.Id);

        Assert.Equal(second.Id, store.Close(third.Id));
        Assert.Equal([first.Id, second.Id], store.ProjectIds);
        Assert.Equal(second.Id, new StudioOpenWorkspaceStore(dataDirectory, new StudioProjectStore(dataDirectory)).SelectedProjectId);
    }

    [Fact]
    public void Open_RejectsCatalogProjectsAndEnforcesMaximumTabCount()
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "studio");
        var projects = new StudioProjectStore(dataDirectory);
        var catalogProject = projects.Create("Catalog");
        var store = new StudioOpenWorkspaceStore(dataDirectory, projects);
        Assert.Throws<InvalidOperationException>(() => store.Open(catalogProject.Id));

        for (var index = 0; index < StudioOpenWorkspaceStore.MaximumOpenProjects; index++)
            store.Open(AddFolderProject(projects, $"Folder {index}").Id);

        var overflow = AddFolderProject(projects, "Overflow");
        var error = Assert.Throws<InvalidOperationException>(() => store.Open(overflow.Id));
        Assert.Contains("up to 8 open folders", error.Message, StringComparison.Ordinal);
        Assert.Equal(StudioOpenWorkspaceStore.MaximumOpenProjects, store.ProjectIds.Count);
    }

    [Fact]
    public void Load_PrunesArchivedAndUnknownFoldersFromLocalWorkspaceState()
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "studio");
        var projects = new StudioProjectStore(dataDirectory);
        var kept = AddFolderProject(projects, "Kept");
        var archived = AddFolderProject(projects, "Archived");
        var store = new StudioOpenWorkspaceStore(dataDirectory, projects);
        store.Open(kept.Id);
        store.Open(archived.Id);
        projects.SetArchived(archived.Id, true);

        var reopened = new StudioOpenWorkspaceStore(dataDirectory, new StudioProjectStore(dataDirectory));

        Assert.Equal([kept.Id], reopened.ProjectIds);
        Assert.Equal(kept.Id, reopened.SelectedProjectId);
    }

    private StudioProject AddFolderProject(StudioProjectStore projects, string name)
    {
        var folder = Path.Combine(directory, "folders", name.Replace(' ', '-'));
        Directory.CreateDirectory(folder);
        return projects.RegisterFolderSource(folder, name);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
