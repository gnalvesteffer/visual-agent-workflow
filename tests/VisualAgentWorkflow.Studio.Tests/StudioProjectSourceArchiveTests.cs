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
