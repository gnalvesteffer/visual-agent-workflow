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
    public void InterfaceCallRelease_PreservesTransitivePinnedRequirementsWithoutProjectAssets()
    {
        var definition = InterfaceCallWorkflow();
        var child = definition.Nodes["call"].WorkflowInterfaceCall!.Implementation.Definition.Operations["lookup"];
        var workflowPackage = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(
            definition, WorkflowLayout.Create(definition)));
        var releaseBytes = WorkflowProjectPackageJson.CreateArchive("project-1", "Interfaces", [workflowPackage]);
        var release = WorkflowProjectPackageJson.Read(releaseBytes);
        var loadedPackage = release.Workflows[definition.WorkflowId];

        Assert.Contains(loadedPackage.Requirements, requirement => requirement.Kind == "workflow-interface" &&
            requirement.Id == "sample.api" && requirement.Version == "1" &&
            requirement.ContractHash == definition.Nodes["call"].WorkflowInterfaceCall!.Implementation.Definition.Interface.ContentHash);
        Assert.Contains(loadedPackage.Requirements, requirement => requirement.Kind == "workflow-interface-implementation" &&
            requirement.Id == "sample.impl" && requirement.Version == "1" &&
            requirement.ContractHash == definition.Nodes["call"].WorkflowInterfaceCall!.Implementation.ContentHash);
        Assert.Contains(loadedPackage.Requirements, requirement => requirement.Kind == "workflow-revision" &&
            requirement.Id == child.WorkflowId && requirement.Version == child.Version.ToString() &&
            requirement.ContractHash == child.ContentHash);
        Assert.Equal(WorkflowJson.ComputeHash(child.Definition), WorkflowJson.ComputeHash(loadedPackage.Definition.Nodes["call"]
            .WorkflowInterfaceCall!.Implementation.Definition.Operations["lookup"].Definition));
        Assert.True(loadedPackage.Requirements.ToHashSet().SetEquals(release.Manifest.Requirements));
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

    private static WorkflowDefinition InterfaceCallWorkflow()
    {
        var contract = WorkflowInterfaceRevision.Create(new WorkflowInterfaceDefinition("sample.api", "Sample API", "Package proof.",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty.Add("lookup",
                new("Lookup", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
                {
                    Inputs = [new("query", "Query", WorkflowDataTypes.Json)],
                    Outputs = [new("result", "Result", WorkflowDataTypes.Json)]
                })) { SchemaVersion = WorkflowInterfaceJson.FieldContractSchemaVersion });
        var child = JsonWorkflow("interface-child");
        var pin = WorkflowInterfaceWorkflowPin.From(child);
        var implementation = WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation("sample.impl", contract,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("lookup", pin)));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "interface-caller", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
                ["call"] = new(NodeTypes.WorkflowInterfaceCall, "1.0.0")
                { WorkflowInterfaceCall = new(implementation, "lookup") },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-call"] = new(new("input", "out"), new("call", "query"), "data"),
                ["call-output"] = new(new("call", "result"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-call"] = new(new("input", "then"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition;
    }

    private static WorkflowDefinition JsonWorkflow(string workflowId) =>
        new(WorkflowSchemaVersions.CurrentAuthoringSchema, workflowId, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["value"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["complete"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

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
