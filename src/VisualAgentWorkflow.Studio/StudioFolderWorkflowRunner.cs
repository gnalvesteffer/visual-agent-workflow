using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;

namespace VisualAgentWorkflow.Studio;

/// <summary>Starts runs from one freshly validated immutable folder source capture.</summary>
public sealed class StudioFolderWorkflowRunner(LocalWorkflowRuntime runtime, StudioProjectSnapshotStore snapshots)
{
    public Task<RunSnapshot> StartSavedAsync(string projectDirectory, string projectId, string workflowId,
        string expectedManifestHash, string expectedAssetHash, string input,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        StartCapturedAsync(CaptureSaved(projectDirectory, projectId, workflowId, expectedManifestHash, expectedAssetHash), input, parameters);

    public Task<RunSnapshot> StartRestorePointAsync(string projectDirectory, string projectId, string workflowId,
        string snapshotContentHash, string input, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var capture = snapshots.ReadSourceSnapshot(projectDirectory, snapshotContentHash);
        return StartCapturedAsync(CaptureWorkflow(capture.Manifest, capture.Files, "restore-point", snapshotContentHash,
            projectId, workflowId), input, parameters);
    }

    private Task<RunSnapshot> StartCapturedAsync(CapturedWorkflow captured, string input,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var revision = WorkflowRevision.Create(captured.Definition, "local-developer") with
        {
            Layout = captured.Layout,
            SourceProvenance = captured.Provenance
        };
        return runtime.StartRevisionWithParametersAsync(revision, input, parameters);
    }

    private static CapturedWorkflow CaptureSaved(string projectDirectory, string projectId, string workflowId,
        string expectedManifestHash, string expectedAssetHash)
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(Path.GetFullPath(projectDirectory));
        var manifestHash = StudioProjectSourceArchive.ComputeManifestHash(manifest);
        if (!string.Equals(manifest.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(manifestHash, expectedManifestHash, StringComparison.Ordinal))
            throw new StudioFolderWorkflowConflictException(workflowId,
                "The project folder changed since it was loaded. Reload the folder before running the saved workflow.");
        var captured = CaptureWorkflow(manifest, files, "folder", null, projectId, workflowId);
        if (!string.Equals(captured.Provenance.AssetHash, expectedAssetHash, StringComparison.Ordinal))
            throw new StudioFolderWorkflowConflictException(workflowId,
                "The selected workflow changed since it was loaded. Reload it before running.");
        return captured;
    }

    private static CapturedWorkflow CaptureWorkflow(StudioProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files, string sourceKind, string? snapshotContentHash,
        string projectId, string workflowId)
    {
        if (!string.Equals(manifest.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidDataException("The selected source belongs to a different project.");
        var file = manifest.Files.SingleOrDefault(item => item.Kind == "workflow" && item.Id == workflowId)
            ?? throw new KeyNotFoundException("The selected workflow is not present in this source snapshot.");
        var bytes = files[file.Path];
        if (!string.Equals(StudioProjectSourceArchive.ComputeContentHash(bytes), file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The selected workflow source failed its content-hash check.");
        var source = StudioProjectSourceArchive.DeserializeWorkflowSource(bytes);
        if (source.Definition.WorkflowId != workflowId)
            throw new InvalidDataException("The workflow source identity does not match the project index.");
        WorkflowValidator.ThrowIfInvalid(source.Definition);
        var manifestHash = StudioProjectSourceArchive.ComputeManifestHash(manifest);
        var layoutHash = WorkflowLayoutJson.ComputeHash(source.Layout, source.Definition);
        var provenance = new WorkflowRunProvenance(sourceKind, projectId, workflowId, manifestHash,
            file.Sha256, layoutHash, snapshotContentHash);
        return new(source.Definition, source.Layout, provenance);
    }

    private sealed record CapturedWorkflow(WorkflowDefinition Definition, WorkflowLayout Layout,
        WorkflowRunProvenance Provenance);
}
