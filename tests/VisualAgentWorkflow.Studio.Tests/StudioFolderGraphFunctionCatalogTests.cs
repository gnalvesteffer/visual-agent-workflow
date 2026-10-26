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

        var saved = source.SaveNewVersion(CreateFunction(1), "Text/Formatting", manifestHash);
        var listed = Assert.Single(source.List());
        var listedAsset = Assert.Single(source.ListAssets());

        Assert.Equal("text.append-suffix", listed.Function.Definition.FunctionId);
        Assert.Equal(saved.ContentHash, listedAsset.ContentHash);
        Assert.Equal("functions/text.append-suffix/1-6b86b273-" + saved.ContentHash + ".json", saved.RelativePath);
        Assert.Equal(saved.RelativePath, StudioProjectSourceDirectory.Read(directory).Manifest.Files.Single().Path);
        Assert.Equal(GraphFunctionJson.ComputeHash(listed.Function.Definition), listed.Function.ContentHash);
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
