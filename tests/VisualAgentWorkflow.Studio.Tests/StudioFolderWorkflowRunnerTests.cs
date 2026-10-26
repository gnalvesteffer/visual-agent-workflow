using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioFolderWorkflowRunnerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-folder-run-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavedWorkflowRun_UsesFreshlyVerifiedSourceAndPersistsItsLayoutProvenance()
    {
        var projectFolder = CreateProject();
        var source = new StudioFolderWorkflowSource(projectFolder);
        var workflow = SaveWorkflow(source, "reverse");
        var current = StudioProjectSourceDirectory.Read(projectFolder).Manifest;
        var journal = new FileRunJournal(Path.Combine(directory, "host-runs"));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var runner = new StudioFolderWorkflowRunner(runtime, new StudioProjectSnapshotStore());
            var queued = await runner.StartSavedAsync(projectFolder, "folder-project", workflow.WorkflowId,
                StudioProjectSourceArchive.ComputeManifestHash(current), workflow.ContentHash, "hello");

            var result = await WaitForRun(runtime, queued.Header.RunId);
            var reopened = new FileRunJournal(Path.Combine(directory, "host-runs")).Get(result.Header.RunId)!;

            Assert.Equal("completed", result.Status);
            Assert.Equal("olleh", result.Events[^1].Data);
            Assert.Equal(workflow.ContentHash, result.Header.SourceProvenance!.AssetHash);
            Assert.Equal("folder", result.Header.SourceProvenance.SourceKind);
            Assert.Equal(WorkflowLayoutJson.Serialize(workflow.Layout, workflow.Definition),
                WorkflowLayoutJson.Serialize(result.Header.Layout!, result.Header.Definition));
            Assert.Equal(WorkflowLayoutJson.ComputeHash(workflow.Layout, workflow.Definition),
                result.Header.SourceProvenance.LayoutHash);
            Assert.Equal(result.Header.SourceProvenance, reopened.Header.SourceProvenance);
            Assert.Equal(WorkflowLayoutJson.Serialize(workflow.Layout, workflow.Definition),
                WorkflowLayoutJson.Serialize(reopened.Header.Layout!, reopened.Header.Definition));
            Assert.False(Directory.Exists(Path.Combine(projectFolder, ".vaw", "runs")));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RestorePointRun_ExecutesCapturedSourceWithoutRestoringTheActiveProject()
    {
        var projectFolder = CreateProject();
        var source = new StudioFolderWorkflowSource(projectFolder);
        var original = SaveWorkflow(source, "reverse");
        var snapshotStore = new StudioProjectSnapshotStore();
        var restorePoint = snapshotStore.Create(projectFolder);
        var active = SaveWorkflow(source, "uppercase", original.ContentHash);
        var activeManifestHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(projectFolder).Manifest);
        var journal = new FileRunJournal(Path.Combine(directory, "snapshot-host-runs"));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var runner = new StudioFolderWorkflowRunner(runtime, snapshotStore);
            var queued = await runner.StartRestorePointAsync(projectFolder, "folder-project", original.WorkflowId,
                restorePoint.Id, "hello");
            var result = await WaitForRun(runtime, queued.Header.RunId);

            Assert.Equal("olleh", result.Events[^1].Data);
            Assert.Equal("restore-point", result.Header.SourceProvenance!.SourceKind);
            Assert.Equal(restorePoint.Id, result.Header.SourceProvenance.SnapshotContentHash);
            Assert.Equal(original.ContentHash, result.Header.SourceProvenance.AssetHash);
            Assert.Equal(active.ContentHash, Assert.Single(source.List()).ContentHash);
            Assert.Equal(activeManifestHash,
                StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(projectFolder).Manifest));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SavedWorkflowRun_RejectsAStaleManifestBeforeAdmission()
    {
        var projectFolder = CreateProject();
        var source = new StudioFolderWorkflowSource(projectFolder);
        var workflow = SaveWorkflow(source, "reverse");
        var staleManifestHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(projectFolder).Manifest);
        _ = SaveWorkflow(source, "uppercase", workflow.ContentHash);
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "stale-runs")),
            NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var runner = new StudioFolderWorkflowRunner(runtime, new StudioProjectSnapshotStore());
            await Assert.ThrowsAsync<StudioFolderWorkflowConflictException>(() => runner.StartSavedAsync(
                projectFolder, "folder-project", workflow.WorkflowId, staleManifestHash, workflow.ContentHash, "hello"));
            Assert.Empty(await runtime.ListRunsAsync());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SameDefinitionWithDifferentCanvasLayouts_HasDistinctRunSourceIdentity()
    {
        var projectFolder = CreateProject();
        var source = new StudioFolderWorkflowSource(projectFolder);
        var first = SaveWorkflow(source, "reverse");
        var secondLayout = first.Layout with
        {
            Nodes = first.Layout.Nodes.SetItem("input", first.Layout.Nodes["input"] with
                { Position = new GraphPoint(first.Layout.Nodes["input"].Position.X + 24, first.Layout.Nodes["input"].Position.Y) })
        };
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "layout-runs")),
            NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var runner = new StudioFolderWorkflowRunner(runtime, new StudioProjectSnapshotStore());
            var firstManifest = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(projectFolder).Manifest);
            var firstRun = await runner.StartSavedAsync(projectFolder, "folder-project", first.WorkflowId,
                firstManifest, first.ContentHash, "hello");
            var firstResult = await WaitForRun(runtime, firstRun.Header.RunId);
            var second = source.Save(first.Definition, secondLayout, first.ContentHash);
            var secondManifest = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(projectFolder).Manifest);
            var secondRun = await runner.StartSavedAsync(projectFolder, "folder-project", second.WorkflowId,
                secondManifest, second.ContentHash, "hello");
            var secondResult = await WaitForRun(runtime, secondRun.Header.RunId);

            Assert.Equal(firstResult.Header.ContentHash, secondResult.Header.ContentHash);
            Assert.NotEqual(firstResult.Header.SourceProvenance!.AssetHash, secondResult.Header.SourceProvenance!.AssetHash);
            Assert.NotEqual(firstResult.Header.SourceProvenance.LayoutHash, secondResult.Header.SourceProvenance.LayoutHash);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    private string CreateProject()
    {
        var folder = Path.Combine(directory, "project");
        StudioProjectSourceDirectory.Write(folder,
            new StudioProjectManifest("1.0", "folder-project", "Folder project", []),
            new Dictionary<string, byte[]>());
        return folder;
    }

    private static StudioFolderWorkflow SaveWorkflow(StudioFolderWorkflowSource source, string operation,
        string? expectedHash = null)
    {
        var definition = WorkflowTemplates.Create(operation) with { WorkflowId = "folder-run" };
        var layout = WorkflowLayout.Create(definition);
        return source.Save(definition, layout, expectedHash);
    }

    private static async Task<RunSnapshot> WaitForRun(LocalWorkflowRuntime runtime, Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RunSnapshot snapshot;
        do
        {
            await Task.Delay(10, timeout.Token);
            snapshot = (await runtime.GetRunAsync(runId))!;
        } while (!snapshot.IsTerminal);
        return snapshot;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
