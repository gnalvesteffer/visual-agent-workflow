using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioProjectSourceArchiveTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-source-archive-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsync_ExportsDeterministicManifestAndWorkflowAndSkillFiles()
    {
        var projectStore = new StudioProjectStore(directory);
        var project = projectStore.Create("Portable project");
        var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "runs")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalWorkflowRuntime>.Instance);
        var workspace = new StudioProjectWorkspace(directory, projectStore, runtime);
        workspace.Select(project.Id);

        var definition = WorkflowTemplates.Create("uppercase-reverse") with { WorkflowId = "workflow-review" };
        var layout = WorkflowLayout.Create(definition);
        await Assert.IsAssignableFrom<IWorkflowDesignCatalog>(workspace.Catalog).SaveDesignAsync(definition, layout, 0);
        workspace.Skills.Add("source-check", "1.0.0", "Source check", "Confirm source requirements.", "Research");
        workspace.AgentProfiles.Add(AgentProfile.Create("review-agent", "1.0.0", "Review agent", "A portable profile.",
            "demo.scripted", "1.0.0", "Review the request.", ImmutableArray<ToolBindingRequest>.Empty), "Agents");
        workspace.Functions.Add(AppendSuffixFunction(), "Text/Formatting");

        var first = await StudioProjectSourceArchive.CreateAsync(workspace);
        var second = await StudioProjectSourceArchive.CreateAsync(workspace);
        var (manifest, files) = StudioProjectSourceArchive.Read(first);

        Assert.Equal(first, second);
        Assert.Equal(project.Id, manifest.ProjectId);
        Assert.Contains(manifest.Files, file => file.Kind == "workflow" && file.Id == definition.WorkflowId);
        Assert.Contains(manifest.Files, file => file.Kind == "skill" && file.Id == "source-check");
        Assert.Contains(manifest.Files, file => file.Kind == "agent-profile" && file.Id == "review-agent");
        Assert.Contains(manifest.Files, file => file.Kind == "graph-function" && file.Id == "text.append-suffix");
        var workflowFile = Assert.Single(manifest.Files.Where(file => file.Kind == "workflow"));
        var workflow = StudioProjectSourceArchive.DeserializeWorkflowSource(files[workflowFile.Path]);
        Assert.Equal(definition.WorkflowId, workflow.Definition.WorkflowId);
        Assert.Equal(WorkflowLayoutJson.Serialize(layout, definition), WorkflowLayoutJson.Serialize(workflow.Layout, definition));
        Assert.DoesNotContain("runs/", string.Join('\n', files.Keys));
    }

    [Fact]
    public void WorkflowSource_RoundTripsDisconnectedDraftsWithoutMakingThemExecutable()
    {
        var valid = WorkflowTemplates.Create("uppercase-reverse");
        var disconnected = valid with { Connections = valid.Connections.Remove(valid.Connections.Keys.Last()) };
        var layout = WorkflowLayout.Create(disconnected);

        var restored = StudioProjectSourceArchive.DeserializeWorkflowSource(
            Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeWorkflowSource(disconnected, layout)));

        Assert.Equal(disconnected.Connections.Count, restored.Definition.Connections.Count);
        Assert.NotEmpty(WorkflowValidator.Validate(restored.Definition));
    }

    [Fact]
    public async Task ImportAsNewProject_RestoresWorkflowAndReusableAssetsIntoAnIsolatedProject()
    {
        var sourceStore = new StudioProjectStore(Path.Combine(directory, "source"));
        var sourceProject = sourceStore.Create("Portable source");
        var sourceData = Path.Combine(directory, "source");
        var sourceRuntime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(sourceData, "runs")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalWorkflowRuntime>.Instance);
        var sourceWorkspace = new StudioProjectWorkspace(sourceData, sourceStore, sourceRuntime);
        sourceWorkspace.Select(sourceProject.Id);
        var definition = WorkflowTemplates.Create("uppercase-reverse") with { WorkflowId = "workflow-imported" };
        var layout = WorkflowLayout.Create(definition);
        await Assert.IsAssignableFrom<IWorkflowDesignCatalog>(sourceWorkspace.Catalog).SaveDesignAsync(definition, layout, 0);
        sourceWorkspace.Skills.Add("source-check", "1.0.0", "Source check", "Confirm source requirements.", "Research");
        sourceWorkspace.AgentProfiles.Add(AgentProfile.Create("review-agent", "1.0.0", "Review agent", "A portable profile.",
            "demo.scripted", "1.0.0", "Review the request.", ImmutableArray<ToolBindingRequest>.Empty), "Agents");
        sourceWorkspace.Functions.Add(AppendSuffixFunction(), "Text/Formatting");
        var archive = await StudioProjectSourceArchive.CreateAsync(sourceWorkspace);

        var targetData = Path.Combine(directory, "target");
        var targetStore = new StudioProjectStore(targetData);
        var targetJournal = new FileRunJournal(Path.Combine(targetData, "runs"));
        var targetRuntime = new LocalWorkflowRuntime(targetJournal,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalWorkflowRuntime>.Instance);
        var targetWorkspace = new StudioProjectWorkspace(targetData, targetStore, targetRuntime);
        var imported = await StudioProjectSourceArchive.ImportAsNewProjectAsync(archive, targetStore, targetWorkspace);

        Assert.Equal("Portable source", imported.Name);
        Assert.Equal(imported.Id, targetWorkspace.CurrentProjectId);
        Assert.NotEqual(sourceProject.Id, imported.Id);
        Assert.Contains(targetWorkspace.Skills.List(), item => item.Skill.Id == "source-check" && item.Folder == "Research");
        Assert.Contains(targetWorkspace.AgentProfiles.List(), item => item.Profile.Id == "review-agent" && item.Folder == "Agents");
        Assert.Contains(targetWorkspace.Functions.List(), item => item.Function.Definition.FunctionId == "text.append-suffix");
        var restored = await Assert.IsAssignableFrom<IWorkflowDesignCatalog>(targetWorkspace.Catalog).GetDesignAsync(definition.WorkflowId);
        Assert.NotNull(restored);
        Assert.Equal(WorkflowLayoutJson.Serialize(layout, definition), WorkflowLayoutJson.Serialize(restored.Layout, definition));
        Assert.Empty(targetJournal.List());
    }

    [Fact]
    public async Task ImportAsNewProject_RejectsInvalidArchiveBeforeCreatingProject()
    {
        var content = Encoding.UTF8.GetBytes("{}\n");
        var asset = new StudioProjectFile("workflow", "workflow-1", "draft", "workflows/workflow-1.json", new string('0', 64));
        var manifest = new StudioProjectManifest("1.0", "project-1", "Rejected import", [asset]);
        var archive = CreateZip(StudioProjectSourceArchive.SerializeManifest(manifest), (asset.Path, content));
        var projectStore = new StudioProjectStore(directory);
        var runtime = new LocalWorkflowRuntime(new FileRunJournal(Path.Combine(directory, "runs")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalWorkflowRuntime>.Instance);
        var workspace = new StudioProjectWorkspace(directory, projectStore, runtime);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StudioProjectSourceArchive.ImportAsNewProjectAsync(archive, projectStore, workspace));

        Assert.Single(projectStore.List());
        Assert.Equal(StudioProjectStore.DefaultProjectId, workspace.CurrentProjectId);
    }

    [Fact]
    public void SourceDirectory_WriteAndRead_RoundTripValidatedPortableContentsWithoutSidecarData()
    {
        var skill = WorkflowSkill.Create("portable-skill", "1.0.0", "Portable skill", "A portable skill.");
        var assetContent = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new SkillLibraryItem(skill, "Shared"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var asset = new StudioProjectFile("skill", "portable-skill", "1.0.0", "skills/portable-skill.json",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(assetContent)));
        var manifest = new StudioProjectManifest("1.0", "project-files", "Portable files", [asset]);
        var files = new Dictionary<string, byte[]> { [asset.Path] = assetContent };
        var folder = Path.Combine(directory, "portable-folder");

        StudioProjectSourceDirectory.Write(folder, manifest, files);
        var (readManifest, readFiles) = StudioProjectSourceDirectory.Read(folder);

        Assert.Equal(manifest.FormatVersion, readManifest.FormatVersion);
        Assert.Equal(manifest.ProjectId, readManifest.ProjectId);
        Assert.Equal(manifest.ProjectName, readManifest.ProjectName);
        Assert.Equal(manifest.Files.ToArray(), readManifest.Files.ToArray());
        Assert.Equal(assetContent, readFiles[asset.Path]);
        Assert.Equal(".vaw/\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
        Assert.False(Directory.Exists(Path.Combine(folder, ".vaw")));
        Assert.Throws<IOException>(() => StudioProjectSourceDirectory.Write(folder, manifest, files));

        var rejectedFolder = Path.Combine(directory, "rejected-folder");
        var invalidManifest = manifest with { Files = [asset with { Sha256 = new string('0', 64) }] };
        Assert.Throws<InvalidDataException>(() => StudioProjectSourceDirectory.Write(rejectedFolder, invalidManifest, files));
        Assert.False(Directory.Exists(rejectedFolder));
    }

    [Fact]
    public void Read_RejectsContentHashMismatchAndUnsafeManifestPaths()
    {
        var content = Encoding.UTF8.GetBytes("{}\n");
        var asset = new StudioProjectFile("workflow", "workflow-1", "draft", "workflows/workflow-1.json", new string('0', 64));
        var manifest = new StudioProjectManifest("1.0", "project-1", "Project", [asset]);
        var archive = CreateZip(StudioProjectSourceArchive.SerializeManifest(manifest), (asset.Path, content));

        Assert.Throws<InvalidDataException>(() => StudioProjectSourceArchive.Read(archive));
        Assert.Throws<InvalidDataException>(() => StudioProjectSourceArchive.SerializeManifest(manifest with
        {
            Files = [asset with { Path = "workflows/../secret.json" }]
        }));
    }

    [Fact]
    public void SourceDirectory_Read_RejectsAggregateContentBeforeLoadingAllAssets()
    {
        const int assetBytes = StudioProjectSourceArchive.MaximumFileBytes;
        var content = new byte[assetBytes];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var files = Enumerable.Range(0, 11)
            .Select(index => new StudioProjectFile("skill", $"skill-{index}", "1.0.0",
                $"skills/skill-{index}/1.0.0.json", hash))
            .ToImmutableArray();
        var manifest = new StudioProjectManifest("1.0", "large-project", "Large project", files);
        var folder = Path.Combine(directory, "oversized-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, StudioProjectSourceArchive.ManifestFileName),
            StudioProjectSourceArchive.SerializeManifest(manifest));
        foreach (var asset in files)
        {
            var path = Path.Combine(folder, asset.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        var error = Assert.Throws<InvalidDataException>(() => StudioProjectSourceDirectory.Read(folder));

        Assert.Equal("Project source folder exceeds the supported aggregate size.", error.Message);
    }

    [Fact]
    public void FolderWorkflowSource_SavesHashAddressedDraftAndRejectsStaleEdits()
    {
        var folder = Path.Combine(directory, "folder-workflow");
        StudioProjectSourceDirectory.Write(folder,
            new StudioProjectManifest("1.0", "folder-project", "Folder project", []),
            new Dictionary<string, byte[]>());
        var source = new StudioFolderWorkflowSource(folder);
        var initial = WorkflowTemplates.Create("uppercase-reverse") with { WorkflowId = "folder-workflow" };
        var initialLayout = WorkflowLayout.Create(initial);

        var firstSave = source.Save(initial, initialLayout, expectedContentHash: null);
        var editedDefinition = initial with { DisplayName = "Edited in folder" };
        var editedLayout = initialLayout with
        {
            Nodes = initialLayout.Nodes.SetItem("input", initialLayout.Nodes["input"] with { Position = new GraphPoint(240, 180) })
        };
        var secondSave = source.Save(editedDefinition, editedLayout, firstSave.ContentHash);

        Assert.Equal("Edited in folder", Assert.Single(source.List()).Definition.DisplayName);
        Assert.Equal(WorkflowLayoutJson.Serialize(editedLayout, editedDefinition),
            WorkflowLayoutJson.Serialize(Assert.Single(source.List()).Layout, editedDefinition));
        Assert.NotEqual(firstSave.ContentHash, secondSave.ContentHash);
        Assert.NotEqual(firstSave.RelativePath, secondSave.RelativePath);
        Assert.Throws<StudioFolderWorkflowConflictException>(() => source.Save(initial, initialLayout, firstSave.ContentHash));
        Assert.Equal(secondSave.ContentHash, Assert.Single(source.List()).ContentHash);
    }

    [Fact]
    public void DeserializeManifest_RejectsDuplicateProperties()
    {
        var json = """
            {"formatVersion":"1.0","formatVersion":"1.0","projectId":"project-1","projectName":"Project","files":[]}
            """;

        Assert.Throws<InvalidDataException>(() => StudioProjectSourceArchive.DeserializeManifest(Encoding.UTF8.GetBytes(json)));
    }

    private static byte[] CreateZip(string manifest, (string Path, byte[] Content) asset)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in new[] { (StudioProjectSourceArchive.ManifestFileName, Encoding.UTF8.GetBytes(manifest)), asset })
            {
                using var entry = archive.CreateEntry(path).Open();
                entry.Write(content);
            }
        }
        return output.ToArray();
    }

    private static GraphFunctionDefinition AppendSuffixFunction() => new(
        "text.append-suffix", 1, "Append suffix", "Appends a fixed suffix to text.",
        new Dictionary<string, GraphFunctionPort> { ["text"] = new("Text", "string") }
            .ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, GraphFunctionPort> { ["result"] = new("Result", "string") }
            .ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text" },
            ["output"] = new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["result"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
