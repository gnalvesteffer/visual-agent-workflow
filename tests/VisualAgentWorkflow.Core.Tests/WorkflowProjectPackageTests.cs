using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowProjectPackageTests
{
    [Fact]
    public void CreateArchive_IsDeterministicAndReadsSelectedWorkflowPackages()
    {
        var workflows = new[] { Package("zeta"), Package("alpha") };

        var first = WorkflowProjectPackageJson.CreateArchive("project-1", "Support", workflows);
        var second = WorkflowProjectPackageJson.CreateArchive("project-1", "Support", workflows.Reverse());
        var package = WorkflowProjectPackageJson.Read(first);

        Assert.Equal(first, second);
        Assert.Equal("project-1", package.Manifest.ProjectId);
        Assert.Equal(["alpha", "zeta"], package.Workflows.Keys);
        Assert.Equal("alpha", package.Workflows["alpha"].Definition.WorkflowId);
        Assert.Contains(package.Manifest.Requirements,
            requirement => requirement.Kind == "node" && requirement.Id == NodeTypes.Uppercase);
        Assert.Equal(WorkflowProjectPackageJson.ComputeHash(first), package.ArtifactHash);
    }

    [Fact]
    public void CreateArchive_RequiresAnExplicitUniqueWorkflowSelection()
    {
        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.CreateArchive("project", "Project", []));
        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.CreateArchive("project", "Project",
            [Package("same"), Package("same")]));
    }

    [Fact]
    public void Read_RejectsTamperedMissingAndUnlistedFiles()
    {
        var archive = WorkflowProjectPackageJson.CreateArchive("project", "Project", [Package("alpha")]);

        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(TamperPackageContent(archive)));
        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(Rewrite(archive,
            files => files.Remove(files.Keys.Single(path => path.StartsWith("workflows/", StringComparison.Ordinal))))));
        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(Rewrite(archive,
            files => files.Add("unlisted.json", Encoding.UTF8.GetBytes("{}")))));
    }

    [Fact]
    public void Read_RejectsManifestRequirementDriftAndUnknownFormats()
    {
        var archive = WorkflowProjectPackageJson.CreateArchive("project", "Project", [Package("alpha")]);

        var changedRequirement = RewriteManifest(archive, manifest =>
        {
            var requirements = (JsonArray)manifest["requirements"]!;
            requirements[0]!["version"] = "999.0";
        });
        var unknownFormat = RewriteManifest(archive, manifest => manifest["formatVersion"] = "99.0");

        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(changedRequirement));
        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(unknownFormat));
    }

    [Fact]
    public void Read_RejectsUnsafePathsAndDuplicateManifestProperties()
    {
        var archive = WorkflowProjectPackageJson.CreateArchive("project", "Project", [Package("alpha")]);

        Assert.Throws<InvalidDataException>(() => WorkflowProjectPackageJson.Read(Rewrite(archive, files =>
        {
            var packagePath = files.Keys.Single(path => path.StartsWith("workflows/", StringComparison.Ordinal));
            var bytes = files[packagePath];
            files.Remove(packagePath);
            files.Add("../" + packagePath, bytes);
        })));
        Assert.Throws<JsonException>(() => WorkflowProjectPackageJson.Read(Rewrite(archive, files =>
        {
            var manifest = files[WorkflowProjectPackageJson.ManifestPath];
            files[WorkflowProjectPackageJson.ManifestPath] = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(manifest).Replace("\"formatVersion\": \"1.0\",",
                    "\"formatVersion\": \"1.0\", \"formatVersion\": \"1.0\","));
        })));
    }

    private static WorkflowPackage Package(string workflowId)
    {
        var definition = new WorkflowDefinition("1.0", workflowId, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["transform"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-transform"] = new(new("input", "out"), new("transform", "in"), "data"),
                ["transform-output"] = new(new("transform", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));
        return WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
    }

    private static byte[] TamperPackageContent(byte[] archive) => Rewrite(archive, files =>
    {
        var packagePath = files.Keys.Single(path => path.StartsWith("workflows/", StringComparison.Ordinal));
        var content = Encoding.UTF8.GetString(files[packagePath]);
        files[packagePath] = Encoding.UTF8.GetBytes(content.Replace("uppercase", "reverse", StringComparison.Ordinal));
    });

    private static byte[] RewriteManifest(byte[] archive, Action<JsonObject> edit) => Rewrite(archive, files =>
    {
        var manifest = JsonNode.Parse(files[WorkflowProjectPackageJson.ManifestPath])!.AsObject();
        edit(manifest);
        files[WorkflowProjectPackageJson.ManifestPath] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
    });

    private static byte[] Rewrite(byte[] archive, Action<Dictionary<string, byte[]>> edit)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var input = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        foreach (var entry in input.Entries)
        {
            using var content = entry.Open();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            files.Add(entry.FullName, buffer.ToArray());
        }
        edit(files);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (path, bytes) in files)
        {
            using var content = zip.CreateEntry(path).Open();
            content.Write(bytes);
        }
        return output.ToArray();
    }
}
