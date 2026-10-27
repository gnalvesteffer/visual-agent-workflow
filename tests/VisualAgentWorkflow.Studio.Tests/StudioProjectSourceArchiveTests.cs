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
            "demo.scripted", "Review the request.", ImmutableArray<ToolBindingRequest>.Empty), "Agents");
        var function = AppendSuffixFunction();
        var functionDraft = StudioGraphFunctionAsset.Draft(function);
        var functionLayout = WorkflowLayout.Create(functionDraft) with
        {
            Nodes = WorkflowLayout.Create(functionDraft).Nodes.SetItem("output", new(new GraphPoint(1_100, 540), "Return", "Archive layout"))
        };
        workspace.Functions.Add(function, "Text/Formatting", functionLayout);

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
        var functionFile = Assert.Single(manifest.Files.Where(file => file.Kind == "graph-function"));
        var functionAsset = StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(files[functionFile.Path]);
        Assert.Equal(WorkflowLayoutJson.Serialize(functionLayout, functionDraft),
            WorkflowLayoutJson.Serialize(functionAsset.Layout, functionDraft));
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
    public void ProjectFileTree_GroupsManifestFilesAndSortsFoldersBeforeFiles()
    {
        var tree = StudioProjectFileTree.Build(
        [
            new("skill", "research", "1.0.0", "skills/research/1.0.0.skill", new string('a', 64)),
            new("workflow", "workflow-z", "draft", "workflows/z.workflow", new string('b', 64)),
            new("workflow", "workflow-a", "draft", "workflows/a.workflow", new string('c', 64)),
            new("graph-function", "text.format", "1.0.0", "functions/text/format.function", new string('d', 64))
        ]);

        Assert.Equal(["functions", "skills", "workflows"], tree.Select(node => node.Name));
        Assert.Equal("text", tree[0].Children[0].Name);
        Assert.Equal("format.function", tree[0].Children[0].Children[0].Name);
        Assert.Equal("a.workflow", tree[2].Children[0].Name);
        Assert.Equal("z.workflow", tree[2].Children[1].Name);
        Assert.Equal("graph-function", tree[0].Children[0].Children[0].File?.Kind);
        Assert.Equal("skill", tree[1].Children[0].Children[0].File?.Kind);
    }

    [Fact]
    public void FolderOperations_SupportDomainFoldersMarkdownAndGitFriendlyMovesAndCopies()
    {
        var folder = Path.Combine(directory, "domain-project");
        var initial = new StudioProjectManifest("1.0", "domain-project", "Domain project", []);
        StudioProjectSourceDirectory.Write(folder, initial, new Dictionary<string, byte[]>());
        var hash = StudioProjectSourceArchive.ComputeManifestHash(initial);
        _ = StudioProjectSourceTreeOperations.CreateDirectory(folder, "domains/support", hash);
        var currentHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(folder).Manifest);
        var document = StudioProjectSourceTreeOperations.CreateDocumentation(folder, "domains/support/README.md",
            "# Support\n\nProject notes.\n", currentHash);

        var workflowDefinition = WorkflowTemplates.Create("uppercase-reverse") with
        {
            WorkflowId = "support-triage",
            DisplayName = "Support triage"
        };
        currentHash = StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(folder).Manifest);
        var workflow = new StudioFolderWorkflowSource(folder).Save(workflowDefinition, WorkflowLayout.Create(workflowDefinition),
            expectedContentHash: null, targetDirectory: "domains/support/workflows");
        var beforeMove = StudioProjectSourceDirectory.Read(folder).Manifest;

        var moved = StudioProjectSourceTreeOperations.RenameDirectory(folder, "domains/support", "domains/customer-support",
            StudioProjectSourceArchive.ComputeManifestHash(beforeMove));
        var movedDocument = moved.Files.Single(file => file.Id == document.Id);
        var movedWorkflow = moved.Files.Single(file => file.Id == workflow.WorkflowId);
        Assert.Equal("domains/customer-support/README.md", movedDocument.Path);
        Assert.Equal("domains/customer-support/workflows/" + Path.GetFileName(workflow.RelativePath), movedWorkflow.Path);
        Assert.False(File.Exists(Path.Combine(folder, "domains", "support", "README.md")));

        var copied = StudioProjectSourceTreeOperations.CopyDirectory(folder, "domains/customer-support", "domains",
            StudioProjectSourceArchive.ComputeManifestHash(moved));
        var copiedDocument = copied.Files.Single(file => file.Kind == "documentation" && file.Id != document.Id);
        Assert.NotEqual(document.Id, copiedDocument.Id);
        Assert.Contains(copied.Files, file => file.Kind == "workflow" && file.Id != workflow.WorkflowId);
        Assert.Contains(copied.Directories, path => path == "domains/customer-support copy/workflows");

        var validated = StudioProjectSourceDirectory.Read(folder);
        Assert.Equal(StudioProjectSourceArchive.FormatVersion, validated.Manifest.FormatVersion);
        Assert.Contains(validated.Manifest.Directories, path => path == "domains/customer-support");
        Assert.Equal("# Support\n\nProject notes.\n", Encoding.UTF8.GetString(validated.Files[copiedDocument.Path]));
        Assert.DoesNotContain(validated.Manifest.Files, file => file.Path.StartsWith("domains/support/", StringComparison.Ordinal));
    }

    [Fact]
    public void Documentation_RequiresUtf8MarkdownPathAndCanBeEditedInPlace()
    {
        var folder = Path.Combine(directory, "markdown-project");
        var manifest = new StudioProjectManifest("1.0", "markdown-project", "Markdown project", []);
        StudioProjectSourceDirectory.Write(folder, manifest, new Dictionary<string, byte[]>());

        Assert.Throws<ArgumentException>(() => StudioProjectSourceTreeOperations.CreateDocumentation(folder, "docs/notes.txt",
            "notes", StudioProjectSourceArchive.ComputeManifestHash(manifest)));
        var document = StudioProjectSourceTreeOperations.CreateDocumentation(folder, "docs/notes.md", "# Notes",
            StudioProjectSourceArchive.ComputeManifestHash(manifest));
        var updated = StudioProjectSourceTreeOperations.SaveDocumentation(folder, document.Path, "# Updated notes",
            StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(folder).Manifest));

        Assert.Equal(document.Id, updated.Id);
        var snapshot = StudioProjectSourceDirectory.Read(folder);
        Assert.Equal("# Updated notes", Encoding.UTF8.GetString(snapshot.Files[document.Path]));
        Assert.Equal(StudioProjectSourceArchive.ComputeContentHash(snapshot.Files[document.Path]), updated.Sha256);
    }

    [Fact]
    public void GeneratedProjectAssetsUseDescriptiveFileExtensions()
    {
        var hash = new string('a', 64);

        Assert.EndsWith(".workflow", StudioProjectSourceArchive.WorkflowDraftPath("support", hash));
        Assert.EndsWith(".skill", StudioProjectSourceArchive.SkillAssetPath("search", "1.0.0", hash));
        Assert.EndsWith(".agent", StudioProjectSourceArchive.VersionedAssetPath("agents", "triage", "1.0.0", hash));
        Assert.EndsWith(".function", StudioProjectSourceArchive.VersionedAssetPath("functions", "normalize", "1.0.0", hash));
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
            "demo.scripted", "Review the request.", ImmutableArray<ToolBindingRequest>.Empty), "Agents");
        var function = AppendSuffixFunction();
        var functionDraft = StudioGraphFunctionAsset.Draft(function);
        var functionLayout = WorkflowLayout.Create(functionDraft) with
        {
            Nodes = WorkflowLayout.Create(functionDraft).Nodes.SetItem("output", new(new GraphPoint(1_000, 400), "Return", "Imported layout"))
        };
        sourceWorkspace.Functions.Add(function, "Text/Formatting", functionLayout);
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
        var importedFunction = Assert.Single(targetWorkspace.Functions.ListAssets());
        Assert.Equal(WorkflowLayoutJson.Serialize(functionLayout, functionDraft),
            WorkflowLayoutJson.Serialize(importedFunction.Layout, functionDraft));
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
    public void ProjectSnapshot_RestoresTheValidatedManifestWithoutOverwritingUnindexedFiles()
    {
        var folder = Path.Combine(directory, "snapshot-project");
        var first = CreateSkillProject(folder, "1.0.0");
        var snapshots = new StudioProjectSnapshotStore();
        var snapshot = snapshots.Create(folder);
        var unindexedPath = Path.Combine(folder, "notes.txt");
        File.WriteAllText(unindexedPath, "Keep this unindexed file.");

        var second = new StudioFolderSkillSource(folder).SaveNewVersion("snapshot-skill", "2.0.0",
            "Snapshot skill", "Second revision.", "", first.ManifestHash);
        var currentManifestHash = StudioProjectSourceArchive.ComputeManifestHash(
            StudioProjectSourceDirectory.Read(folder).Manifest);
        var restored = snapshots.Restore(folder, snapshot.Id, currentManifestHash);
        var (restoredManifest, restoredFiles) = StudioProjectSourceDirectory.Read(folder);

        Assert.True(restored.IsValid);
        Assert.Equal(snapshot.ManifestHash, StudioProjectSourceArchive.ComputeManifestHash(restoredManifest));
        Assert.Contains(restoredManifest.Files, file => file.Id == "snapshot-skill" && file.Version == "1.0.0");
        Assert.DoesNotContain(restoredManifest.Files, file => file.Id == "snapshot-skill" && file.Version == "2.0.0");
        Assert.Contains(first.AssetPath, restoredFiles.Keys);
        Assert.True(File.Exists(Path.Combine(folder, second.RelativePath)));
        Assert.Equal("Keep this unindexed file.", File.ReadAllText(unindexedPath));
        Assert.Contains(snapshots.List(folder), item => item.Id == snapshot.Id && item.IsValid);
    }

    [Fact]
    public void ProjectSnapshot_RejectsStaleRestorePreviewAndSidecarDeletionDoesNotInvalidateProject()
    {
        var folder = Path.Combine(directory, "snapshot-deletion-project");
        var first = CreateSkillProject(folder, "1.0.0");
        var snapshots = new StudioProjectSnapshotStore();
        var snapshot = snapshots.Create(folder);
        var second = new StudioFolderSkillSource(folder).SaveNewVersion("snapshot-skill", "2.0.0",
            "Snapshot skill", "Second revision.", "", first.ManifestHash);

        Assert.Throws<IOException>(() => snapshots.Restore(folder, snapshot.Id, first.ManifestHash));
        Assert.Contains(second.RelativePath, StudioProjectSourceDirectory.Read(folder).Manifest.Files.Select(file => file.Path));

        Directory.Delete(Path.Combine(folder, ".vaw"), recursive: true);

        Assert.Empty(snapshots.List(folder));
        Assert.Equal("snapshot-deletion-project", StudioProjectSourceDirectory.Read(folder).Manifest.ProjectId);
        Assert.NotEmpty(StudioProjectSourceArchive.CreateArchive(
            StudioProjectSourceDirectory.Read(folder).Manifest, StudioProjectSourceDirectory.Read(folder).Files));
    }

    [Fact]
    public void ProjectSnapshot_ReportsCorruptSidecarEntriesWithoutInvalidatingSource()
    {
        var folder = Path.Combine(directory, "snapshot-corrupt-project");
        CreateSkillProject(folder, "1.0.0");
        var snapshots = new StudioProjectSnapshotStore();
        var snapshot = snapshots.Create(folder);
        File.WriteAllText(Path.Combine(folder, ".vaw", "snapshots", snapshot.Id + ".vawsrc.zip"), "corrupt");

        var entry = Assert.Single(snapshots.List(folder));

        Assert.False(entry.IsValid);
        Assert.NotNull(entry.Problem);
        Assert.Equal("snapshot-corrupt-project", StudioProjectSourceDirectory.Read(folder).Manifest.ProjectId);
    }

    [Fact]
    public void ProjectSnapshot_RequiresTheSidecarIgnoreRuleWithoutDiscardingExistingIgnoreRules()
    {
        var folder = Path.Combine(directory, "snapshot-ignore-project");
        var project = CreateSkillProject(folder, "1.0.0");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");

        new StudioProjectSnapshotStore().Create(folder);

        Assert.Equal("bin/\nobj/\n/.vaw/\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(folder, project.AssetPath)));
    }

    private static (string ManifestHash, string AssetPath) CreateSkillProject(string folder, string version)
    {
        var content = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(
            new SkillLibraryItem(WorkflowSkill.Create("snapshot-skill", version, "Snapshot skill", "First revision."), "")));
        var hash = StudioProjectSourceArchive.ComputeContentHash(content);
        var path = StudioProjectSourceArchive.SkillAssetPath("snapshot-skill", version, hash);
        var asset = new StudioProjectFile("skill", "snapshot-skill", version, path, hash);
        var manifest = new StudioProjectManifest("1.0", Path.GetFileName(folder), "Snapshot project", [asset]);
        StudioProjectSourceDirectory.Write(folder, manifest, new Dictionary<string, byte[]> { [path] = content });
        return (StudioProjectSourceArchive.ComputeManifestHash(manifest), path);
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
    public void FolderSkillSource_SavesImmutableVersionWithoutChangingCatalogAndRejectsStaleManifest()
    {
        var folder = Path.Combine(directory, "folder-skills");
        var originalItem = new SkillLibraryItem(
            WorkflowSkill.Create("review", "1.0.0", "Review", "Check the original requirements."), "Research");
        var originalContent = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(originalItem));
        var originalHash = StudioProjectSourceArchive.ComputeContentHash(originalContent);
        var originalPath = StudioProjectSourceArchive.SkillAssetPath("review", "1.0.0", originalHash);
        var originalFile = new StudioProjectFile("skill", "review", "1.0.0", originalPath, originalHash);
        var initialManifest = new StudioProjectManifest("1.0", "folder-skills", "Folder skills", [originalFile]);
        StudioProjectSourceDirectory.Write(folder, initialManifest, new Dictionary<string, byte[]> { [originalPath] = originalContent });

        var source = new StudioFolderSkillSource(folder);
        var library = new StudioSkillLibrary(Path.Combine(directory, "catalog-skills"));
        var expectedManifestHash = StudioProjectSourceArchive.ComputeManifestHash(initialManifest);
        var saved = source.SaveNewVersion("review", "2.0.0", "Review", "Check updated requirements.", "Research",
            expectedManifestHash);

        var versions = source.List();
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, versions.Select(skill => skill.Item.Skill.Version).OrderBy(version => version));
        Assert.Equal(originalContent, File.ReadAllBytes(Path.Combine(folder, originalPath)));
        Assert.NotEqual(originalPath, saved.RelativePath);
        Assert.Equal(saved.ContentHash, StudioProjectSourceArchive.ComputeContentHash(
            File.ReadAllBytes(Path.Combine(folder, saved.RelativePath))));
        Assert.Equal(saved.Item.Skill, WorkflowSkill.Create("review", "2.0.0", "Review", "Check updated requirements."));
        Assert.Empty(library.List());

        var currentManifestBytes = File.ReadAllBytes(Path.Combine(folder, StudioProjectSourceArchive.ManifestFileName));
        Assert.Throws<StudioFolderSkillConflictException>(() => source.SaveNewVersion("review", "3.0.0", "Review",
            "A stale edit.", "Research", expectedManifestHash));
        Assert.Equal(currentManifestBytes, File.ReadAllBytes(Path.Combine(folder, StudioProjectSourceArchive.ManifestFileName)));
        Assert.Throws<ArgumentException>(() => source.SaveNewVersion("review", "2.0.0", "Review",
            "Duplicate revision.", "Research", StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(folder).Manifest)));
    }

    [Fact]
    public void FolderAgentProfileSource_SavesImmutableProfileAndExposesItOnlyToFolderConsumers()
    {
        var folder = Path.Combine(directory, "folder-agent-profiles");
        var manifest = new StudioProjectManifest("1.0", "folder-agents", "Folder agents", []);
        StudioProjectSourceDirectory.Write(folder, manifest, new Dictionary<string, byte[]>());
        var source = new StudioFolderAgentProfileSource(folder);
        var catalog = new StudioAgentProfileLibrary(Path.Combine(directory, "catalog-agent-profiles"));
        var original = AgentProfile.Create("review-agent", "1.0.0", "Review agent", "Reviews a request.",
            "model.primary", providerVersion: null, "Review the supplied request.", ImmutableArray<ToolBindingRequest>.Empty,
            ImmutableArray<WorkflowSkill>.Empty);
        var first = source.SaveNewVersion(original, "Agents/Review", StudioProjectSourceArchive.ComputeManifestHash(manifest));

        var revised = AgentProfile.Create("review-agent", "2.0.0", "Review agent", "Reviews and summarizes a request.",
            "model.primary", providerVersion: null, "Review the supplied request and return a concise summary.", ImmutableArray<ToolBindingRequest>.Empty,
            ImmutableArray<WorkflowSkill>.Empty);
        var second = source.SaveNewVersion(revised, "Agents/Review", StudioProjectSourceArchive.ComputeManifestHash(
            StudioProjectSourceDirectory.Read(folder).Manifest));
        var (currentManifest, currentFiles) = StudioProjectSourceDirectory.Read(folder);

        Assert.Equal(new[] { "1.0.0", "2.0.0" }, source.List().Select(profile => profile.Item.Profile.Version).OrderBy(version => version));
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Contains(currentManifest.Files, file => file.Path == first.RelativePath);
        Assert.Equal(second.ContentHash, StudioProjectSourceArchive.ComputeContentHash(currentFiles[second.RelativePath]));
        Assert.Contains(((IAgentProfileLibrary)source).List(), item => item.Profile.ContentHash == second.Item.Profile.ContentHash);
        Assert.Empty(catalog.List());
        Assert.Throws<ArgumentException>(() => source.SaveNewVersion(revised, "Agents/Review",
            StudioProjectSourceArchive.ComputeManifestHash(currentManifest)));
        Assert.Throws<StudioFolderAgentProfileConflictException>(() => source.SaveNewVersion(
            AgentProfile.Create("review-agent", "3.0.0", "Review agent", "Stale write.", "model.primary", null,
                "Review the supplied request.", ImmutableArray<ToolBindingRequest>.Empty),
            "Agents/Review", StudioProjectSourceArchive.ComputeManifestHash(manifest)));
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
