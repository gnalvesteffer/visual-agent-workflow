using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public sealed record WorkflowInterfaceAuthoringAsset(WorkflowInterfaceRevision Revision, string Detail);
public sealed record WorkflowInterfaceImplementationAuthoringAsset(WorkflowInterfaceImplementationRevision Revision, string Detail);
public sealed record WorkflowInterfaceWorkflowChoice(string SelectionId, string DisplayName, string Detail,
    WorkflowInterfaceWorkflowPin Pin);

/// <summary>Storage-neutral inputs and immutable save operations for the shared interface authoring form.</summary>
public interface IWorkflowInterfaceAuthoringSource
{
    IReadOnlyList<WorkflowInterfaceAuthoringAsset> Interfaces { get; }
    IReadOnlyList<WorkflowInterfaceImplementationAuthoringAsset> Implementations { get; }
    IReadOnlyList<WorkflowInterfaceWorkflowChoice> Workflows { get; }
    WorkflowInterfaceRevision SaveInterfaceRevision(WorkflowInterfaceRevision revision);
    WorkflowInterfaceImplementationRevision SaveImplementationRevision(WorkflowInterfaceImplementationRevision revision);
}

/// <summary>Folder adapter keeps manifest concurrency and project-relative workflow identity explicit.</summary>
public sealed class StudioFolderWorkflowInterfaceAuthoringSource : IWorkflowInterfaceAuthoringSource
{
    private readonly StudioFolderWorkflowInterfaceSource source;
    private readonly string directory;
    private string expectedManifestHash;
    public IReadOnlyList<WorkflowInterfaceAuthoringAsset> Interfaces { get; private set; } = [];
    public IReadOnlyList<WorkflowInterfaceImplementationAuthoringAsset> Implementations { get; private set; } = [];
    public IReadOnlyList<WorkflowInterfaceWorkflowChoice> Workflows { get; }

    public StudioFolderWorkflowInterfaceAuthoringSource(string directory, string expectedManifestHash,
        IEnumerable<StudioFolderWorkflowInterface> interfaces,
        IEnumerable<StudioFolderWorkflowInterfaceImplementation> implementations,
        IEnumerable<StudioFolderWorkflow> workflows)
    {
        this.directory = directory;
        source = new StudioFolderWorkflowInterfaceSource(directory);
        this.expectedManifestHash = expectedManifestHash;
        RefreshAssets(interfaces, implementations);
        Workflows = workflows.Select(item => new WorkflowInterfaceWorkflowChoice(item.WorkflowId, item.DisplayName,
            item.RelativePath, WorkflowInterfaceWorkflowPin.From(item.Definition))).ToArray();
    }

    public WorkflowInterfaceRevision SaveInterfaceRevision(WorkflowInterfaceRevision revision)
    {
        var saved = source.SaveInterfaceRevision(revision, expectedManifestHash).Revision;
        RefreshAfterSave();
        return saved;
    }

    public WorkflowInterfaceImplementationRevision SaveImplementationRevision(WorkflowInterfaceImplementationRevision revision)
    {
        var saved = source.SaveImplementationRevision(revision, expectedManifestHash).Revision;
        RefreshAfterSave();
        return saved;
    }

    private void RefreshAfterSave()
    {
        var (manifest, _) = StudioProjectSourceDirectory.Read(directory);
        expectedManifestHash = StudioProjectSourceArchive.ComputeManifestHash(manifest);
        RefreshAssets(source.ListInterfaces(), source.ListImplementations());
    }

    private void RefreshAssets(IEnumerable<StudioFolderWorkflowInterface> interfaces,
        IEnumerable<StudioFolderWorkflowInterfaceImplementation> implementations)
    {
        Interfaces = interfaces.Select(item => new WorkflowInterfaceAuthoringAsset(item.Revision, item.RelativePath)).ToArray();
        Implementations = implementations.Select(item => new WorkflowInterfaceImplementationAuthoringAsset(item.Revision, item.RelativePath)).ToArray();
    }
}

/// <summary>Catalog adapter binds only immutable published workflow revisions.</summary>
public sealed class StudioCatalogWorkflowInterfaceAuthoringSource : IWorkflowInterfaceAuthoringSource
{
    private readonly StudioWorkflowInterfaceLibrary library;
    public IReadOnlyList<WorkflowInterfaceAuthoringAsset> Interfaces { get; private set; } = [];
    public IReadOnlyList<WorkflowInterfaceImplementationAuthoringAsset> Implementations { get; private set; } = [];
    public IReadOnlyList<WorkflowInterfaceWorkflowChoice> Workflows { get; }

    public StudioCatalogWorkflowInterfaceAuthoringSource(StudioWorkflowInterfaceLibrary library,
        IEnumerable<PublishedWorkflowRevision> workflows)
    {
        this.library = library;
        RefreshAssets();
        Workflows = workflows.Select(item => new WorkflowInterfaceWorkflowChoice(item.Revision.RevisionId.ToString(),
            item.Revision.Definition.DisplayName ?? item.Revision.Definition.WorkflowId,
            $"Published revision {item.Version} · {item.Revision.RevisionId:N}", WorkflowInterfaceWorkflowPin.From(item))).ToArray();
    }

    public WorkflowInterfaceRevision SaveInterfaceRevision(WorkflowInterfaceRevision revision)
    {
        library.Add(revision);
        RefreshAssets();
        return revision;
    }

    public WorkflowInterfaceImplementationRevision SaveImplementationRevision(WorkflowInterfaceImplementationRevision revision)
    {
        library.Add(revision);
        RefreshAssets();
        return revision;
    }

    private void RefreshAssets()
    {
        Interfaces = library.ListInterfaceRevisions().Select(item => new WorkflowInterfaceAuthoringAsset(item,
            $"Interface revision {item.RevisionId:N}")).ToArray();
        Implementations = library.ListImplementationRevisions().Select(item =>
            new WorkflowInterfaceImplementationAuthoringAsset(item, $"Implementation revision {item.RevisionId:N}")).ToArray();
    }
}
