using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioFolderGraphFunctionCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-folder-functions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveNewVersion_WritesImmutableContentAddressedAssetAndExposesItToWorkflowEditing()
    {
        WriteEmptyProject();
        var source = new StudioFolderGraphFunctionCatalog(directory);
        var manifestHash = ManifestHash();
        var function = CreateFunction(1);
        var draft = StudioGraphFunctionAsset.Draft(function);
        var layout = WorkflowLayout.Create(draft) with
        {
            Nodes = WorkflowLayout.Create(draft).Nodes.SetItem("concat", new(new GraphPoint(920, 640), "Join text", "Keep this node near the output.")),
            Sections = ImmutableSortedDictionary<string, GraphSection>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("body", new("Function body", ["concat", "suffix"]))
        };

        var saved = source.SaveNewVersion(function, "Text/Formatting", manifestHash, layout);
        var listed = Assert.Single(source.List());
        var listedAsset = Assert.Single(source.ListAssets());

        Assert.Equal("text.append-suffix", listed.Function.Definition.FunctionId);
        Assert.Equal(saved.ContentHash, listedAsset.ContentHash);
        Assert.Equal("functions/text.append-suffix/1-6b86b273-" + saved.ContentHash + ".json", saved.RelativePath);
        Assert.Equal(saved.RelativePath, StudioProjectSourceDirectory.Read(directory).Manifest.Files.Single().Path);
        Assert.Equal(GraphFunctionJson.ComputeHash(listed.Function.Definition), listed.Function.ContentHash);
        Assert.Equal(WorkflowLayoutJson.Serialize(layout, draft), WorkflowLayoutJson.Serialize(listedAsset.Layout, draft));
        Assert.Equal(GraphFunctionJson.ComputeHash(function), GraphFunctionJson.ComputeHash(listedAsset.Item.Function.Definition));
        var defaultLayoutHash = StudioProjectSourceArchive.ComputeContentHash(Encoding.UTF8.GetBytes(
            StudioProjectSourceArchive.SerializeGraphFunctionAsset(listedAsset.Item, WorkflowLayout.Create(draft))));
        Assert.NotEqual(saved.ContentHash, defaultLayoutHash);
    }

    [Fact]
    public void SaveNewVersion_PreservesEarlierRevisionsAndRejectsDuplicateOrStaleWrites()
    {
        WriteEmptyProject();
        var source = new StudioFolderGraphFunctionCatalog(directory);
        var initialManifestHash = ManifestHash();
        var first = source.SaveNewVersion(CreateFunction(1), "Text", initialManifestHash);
        var second = source.SaveNewVersion(CreateFunction(2), "Text", ManifestHash());

        Assert.Equal(2, source.List().Count);
        Assert.Equal(first.ContentHash, source.ListAssets().Single(asset => asset.Item.Function.Definition.Version == 1).ContentHash);
        Assert.Throws<ArgumentException>(() => source.SaveNewVersion(CreateFunction(1), "Text", ManifestHash()));
        Assert.Throws<StudioFolderGraphFunctionConflictException>(() =>
            source.SaveNewVersion(CreateFunction(3), "Text", initialManifestHash));
        Assert.Equal(2, second.Item.Function.Definition.Version);
    }

    [Fact]
    public void GraphFunctionAsset_ReadsLegacyEntriesAndRejectsInvalidVersionOrLayout()
    {
        var function = CreateFunction(1);
        var entry = new GraphFunctionCatalogEntry(new(function, GraphFunctionJson.ComputeHash(function)), "Text");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var legacy = StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, options)));
        var draft = StudioGraphFunctionAsset.Draft(function);
        Assert.Equal(WorkflowLayoutJson.Serialize(WorkflowLayout.Create(draft), draft),
            WorkflowLayoutJson.Serialize(legacy.Layout, draft));

        var current = StudioGraphFunctionAsset.Create(entry);
        var unsupported = current with { FormatVersion = "99.0" };
        Assert.Throws<InvalidDataException>(() => StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unsupported, options))));

        var invalidLayout = current with { Layout = current.Layout with { Nodes = current.Layout.Nodes.Remove("input") } };
        Assert.Throws<ArgumentException>(() => StudioProjectSourceArchive.DeserializeGraphFunctionSourceAsset(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(invalidLayout, options))));
    }

    private string ManifestHash() => StudioProjectSourceArchive.ComputeManifestHash(StudioProjectSourceDirectory.Read(directory).Manifest);

    private void WriteEmptyProject()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, StudioProjectSourceArchive.ManifestFileName),
            """{"formatVersion":"1.0","projectId":"project-functions","projectName":"Project functions","files":[]}""");
    }

    private static GraphFunctionDefinition CreateFunction(int version) => new(
        "text.append-suffix", version, "Append suffix", "Appends a fixed suffix to text.",
        new Dictionary<string, GraphFunctionPort> { ["text"] = new("Text", "string") }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, GraphFunctionPort> { ["result"] = new("Result", "string") }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text", OutputDataType = "string" },
            ["suffix"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = "string" }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["argument"] = new(new("input", "out"), new("concat", "left"), "data"),
            ["suffix"] = new(new("suffix", "out"), new("concat", "right"), "data"),
            ["result"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
