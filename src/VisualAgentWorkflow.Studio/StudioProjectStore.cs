using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioProject(string Id, string Name, DateTimeOffset CreatedAt, bool IsArchived, string? FolderPath = null)
{
    public bool IsFolderBacked => FolderPath is not null;
}

/// <summary>Local Studio project metadata. Projects organize authoring files; they do not grant host capabilities.</summary>
public sealed class StudioProjectStore
{
    public const string DefaultProjectId = "default";
    private readonly object gate = new();
    private readonly string path;
    private List<StudioProject> projects;

    public StudioProjectStore(string studioDataDirectory)
    {
        Directory.CreateDirectory(studioDataDirectory);
        path = Path.Combine(studioDataDirectory, "projects.json");
        projects = File.Exists(path) ? JsonSerializer.Deserialize<List<StudioProject>>(File.ReadAllText(path)) ?? [] : [];
        if (projects.Any(project => project is null || !ValidId(project.Id) || string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 80 ||
                project.FolderPath is { } folderPath && (!Path.IsPathFullyQualified(folderPath) || Path.GetFullPath(folderPath) != folderPath)) ||
            projects.Select(project => project.Id).Distinct(StringComparer.Ordinal).Count() != projects.Count)
            throw new InvalidDataException("The Studio project catalog contains an invalid project.");
        if (projects.All(project => project.Id != DefaultProjectId))
        {
            projects.Insert(0, new(DefaultProjectId, "Default Project", DateTimeOffset.UnixEpoch, false));
            Save(projects);
        }
    }

    public IReadOnlyList<StudioProject> List()
    {
        lock (gate) return projects.OrderBy(project => project.Id == DefaultProjectId ? 0 : 1)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public StudioProject GetActive(string id)
    {
        lock (gate) return projects.SingleOrDefault(project => project.Id == id && !project.IsArchived)
            ?? throw new KeyNotFoundException("The selected project does not exist or is archived.");
    }

    public StudioProject Create(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a project name between 1 and 80 characters.", nameof(name));
        lock (gate)
        {
            if (projects.Any(project => !project.IsArchived && string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active project already has this name.", nameof(name));
            var project = new StudioProject("project-" + Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow, false);
            Save([.. projects, project]);
            return project;
        }
    }

    public StudioProject RegisterFolderSource(string folderPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("The folder project name must contain between 1 and 80 printable characters.", nameof(name));
        var canonicalPath = Path.GetFullPath(folderPath);
        lock (gate)
        {
            var existing = projects.SingleOrDefault(project => project.FolderPath is not null &&
                PathComparer.Equals(project.FolderPath, canonicalPath));
            if (existing is not null)
            {
                if (existing.IsArchived)
                {
                    var reactivated = existing with { IsArchived = false };
                    Save(projects.Select(project => project.Id == existing.Id ? reactivated : project).ToList());
                    return reactivated;
                }
                return existing;
            }
            var project = new StudioProject("folder-" + Guid.NewGuid().ToString("N"), name,
                DateTimeOffset.UtcNow, false, canonicalPath);
            Save([.. projects, project]);
            return project;
        }
    }

    public bool SetArchived(string id, bool archived)
    {
        if (id == DefaultProjectId && archived) throw new ArgumentException("The default project cannot be archived.", nameof(id));
        lock (gate)
        {
            var index = projects.FindIndex(project => project.Id == id);
            if (index < 0) return false;
            if (!archived && projects.Any(project => project.Id != id && !project.IsArchived &&
                string.Equals(project.Name, projects[index].Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active project already has this name.", nameof(id));
            var updated = projects.ToList();
            updated[index] = updated[index] with { IsArchived = archived };
            Save(updated);
            return true;
        }
    }

    public string ProjectDirectory(string id)
    {
        var project = GetActive(id);
        if (project.IsFolderBacked) throw new InvalidOperationException("Folder-backed projects store source in their selected folder.");
        return Path.Combine(Path.GetDirectoryName(path)!, "projects", id);
    }

    public string FolderProjectPath(string id)
    {
        var project = GetActive(id);
        return project.FolderPath ?? throw new InvalidOperationException("The selected project is not folder-backed.");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void Save(List<StudioProject> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        projects = updated;
    }

    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 80 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

/// <summary>Resolves project-owned authoring catalogs for a Studio circuit. MCP and model settings remain environment-wide.</summary>
public sealed class StudioProjectWorkspace
{
    private readonly string studioDataDirectory;
    private readonly StudioProjectStore store;
    private readonly LocalWorkflowRuntime runtime;
    private readonly Dictionary<string, ProjectAssets> cache = new(StringComparer.Ordinal);
    private ProjectAssets current;

    public StudioProjectWorkspace(string studioDataDirectory, StudioProjectStore store, LocalWorkflowRuntime runtime)
    {
        this.studioDataDirectory = Path.GetFullPath(studioDataDirectory);
        this.store = store;
        this.runtime = runtime;
        current = Resolve(StudioProjectStore.DefaultProjectId);
    }

    public IReadOnlyList<StudioProject> Projects => store.List();
    public string CurrentProjectId => current.Project.Id;
    public string CurrentProjectName => current.Project.Name;
    public IWorkflowCatalog Catalog => current.Catalog;
    public IWorkflowClient Client => current.Client;
    public StudioSkillLibrary Skills => current.Skills;
    public StudioAgentProfileLibrary AgentProfiles => current.AgentProfiles;
    public StudioGraphFunctionLibrary Functions => current.Functions;
    public bool IsReadOnly => current.Project.IsArchived;

    public void Select(string? projectId)
    {
        var id = string.IsNullOrWhiteSpace(projectId) ? StudioProjectStore.DefaultProjectId : projectId;
        current = Resolve(id);
    }

    private ProjectAssets Resolve(string id)
    {
        var project = store.GetActive(id);
        if (cache.TryGetValue(id, out var cached)) return cached;
        var directory = store.ProjectDirectory(id);
        Directory.CreateDirectory(directory);
        if (id == StudioProjectStore.DefaultProjectId) MigrateLegacyAuthoringFiles(directory);
        var catalog = new SqliteWorkflowCatalog(Path.Combine(directory, "workflows.db"));
        var assets = new ProjectAssets(project, catalog, new PublishedWorkflowClient(catalog, runtime),
            new StudioSkillLibrary(directory), new StudioAgentProfileLibrary(directory), new StudioGraphFunctionLibrary(directory));
        cache.Add(id, assets);
        return assets;
    }

    private void MigrateLegacyAuthoringFiles(string destination)
    {
        var oldDatabase = Path.Combine(studioDataDirectory, "workflows.db");
        var newDatabase = Path.Combine(destination, "workflows.db");
        if (File.Exists(oldDatabase) && !File.Exists(newDatabase))
        {
            var temporaryDatabase = newDatabase + ".migration";
            using var source = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = oldDatabase, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var target = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = temporaryDatabase, Pooling = false }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);
            target.Close();
            File.Move(temporaryDatabase, newDatabase, true);
        }

        foreach (var file in new[] { "skills.json", "agent-profiles.json", "graph-functions.json" })
        {
            var source = Path.Combine(studioDataDirectory, file);
            var target = Path.Combine(destination, file);
            if (!File.Exists(source) || File.Exists(target)) continue;
            var temporary = target + ".migration";
            File.Copy(source, temporary, true);
            File.Move(temporary, target, true);
        }
    }

    private sealed record ProjectAssets(StudioProject Project, IWorkflowCatalog Catalog, IWorkflowClient Client,
        StudioSkillLibrary Skills, StudioAgentProfileLibrary AgentProfiles, StudioGraphFunctionLibrary Functions);
}
