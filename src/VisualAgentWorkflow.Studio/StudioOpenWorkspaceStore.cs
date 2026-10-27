using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioOpenWorkspaceDocument(int FormatVersion, ImmutableArray<string> ProjectIds, string? SelectedProjectId);

/// <summary>Machine-local open folder tabs. Project folders remain the portable source of truth.</summary>
public sealed class StudioOpenWorkspaceStore
{
    public const int MaximumOpenProjects = 8;
    private const int FormatVersion = 1;
    private const int MaximumDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object gate = new();
    private readonly string path;
    private readonly StudioProjectStore projects;
    private ImmutableArray<string> projectIds = [];
    private string? selectedProjectId;

    public StudioOpenWorkspaceStore(string studioDataDirectory, StudioProjectStore projects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studioDataDirectory);
        ArgumentNullException.ThrowIfNull(projects);
        this.projects = projects;
        Directory.CreateDirectory(studioDataDirectory);
        path = Path.Combine(Path.GetFullPath(studioDataDirectory), "open-workspace.json");
        Load();
    }

    public IReadOnlyList<string> ProjectIds
    {
        get { lock (gate) return projectIds.ToArray(); }
    }

    public string? SelectedProjectId
    {
        get { lock (gate) return selectedProjectId; }
    }

    public void Open(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var project = projects.GetActive(projectId);
        if (!project.IsFolderBacked) throw new InvalidOperationException("Only folder-backed projects can be opened as source workspace tabs.");

        lock (gate)
        {
            if (projectIds.Contains(projectId, StringComparer.Ordinal))
            {
                SelectCore(projectId);
                return;
            }
            if (projectIds.Length >= MaximumOpenProjects)
                throw new InvalidOperationException($"Close a project tab before opening another. Studio supports up to {MaximumOpenProjects} open folders.");
            projectIds = projectIds.Add(projectId);
            SelectCore(projectId);
        }
    }

    public void Select(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (gate) SelectCore(projectId);
    }

    public void Reorder(string projectId, int targetIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (gate)
        {
            var sourceIndex = projectIds.IndexOf(projectId);
            if (sourceIndex < 0) throw new InvalidOperationException("Open the project folder before reordering its tab.");
            if (targetIndex < 0 || targetIndex >= projectIds.Length) throw new ArgumentOutOfRangeException(nameof(targetIndex));
            if (sourceIndex == targetIndex) return;
            var reordered = projectIds.ToBuilder();
            reordered.RemoveAt(sourceIndex);
            reordered.Insert(targetIndex, projectId);
            projectIds = reordered.ToImmutable();
            Save();
        }
    }

    public string? Close(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (gate)
        {
            var index = projectIds.IndexOf(projectId);
            if (index < 0) return selectedProjectId;
            projectIds = projectIds.RemoveAt(index);
            if (selectedProjectId == projectId)
                selectedProjectId = projectIds.IsEmpty ? null : projectIds[Math.Max(0, index - 1)];
            Save();
            return selectedProjectId;
        }
    }

    private void SelectCore(string projectId)
    {
        if (!projectIds.Contains(projectId, StringComparer.Ordinal))
            throw new InvalidOperationException("Open the project folder before selecting its workspace tab.");
        if (selectedProjectId == projectId) return;
        selectedProjectId = projectId;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaximumDocumentBytes)
            throw new InvalidDataException("The local Studio workspace file exceeds the supported size.");
        var document = JsonSerializer.Deserialize<StudioOpenWorkspaceDocument>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("The local Studio workspace file is empty.");
        if (document.FormatVersion != FormatVersion || document.ProjectIds.IsDefault ||
            document.ProjectIds.Length > MaximumOpenProjects || document.ProjectIds.Distinct(StringComparer.Ordinal).Count() != document.ProjectIds.Length ||
            document.ProjectIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("The local Studio workspace file is invalid or uses an unsupported format.");

        var activeProjects = projects.List().Where(project => !project.IsArchived && project.IsFolderBacked)
            .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        projectIds = document.ProjectIds.Where(activeProjects.Contains).ToImmutableArray();
        selectedProjectId = document.SelectedProjectId is not null && projectIds.Contains(document.SelectedProjectId, StringComparer.Ordinal)
            ? document.SelectedProjectId
            : projectIds.FirstOrDefault();
        if (!document.ProjectIds.SequenceEqual(projectIds) || document.SelectedProjectId != selectedProjectId) Save();
    }

    private void Save()
    {
        var document = new StudioOpenWorkspaceDocument(FormatVersion, projectIds, selectedProjectId);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
