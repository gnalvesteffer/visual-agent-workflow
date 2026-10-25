using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioGraphFunctionLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-functions-" + Guid.NewGuid());

    [Fact]
    public void Library_persists_immutable_revisions_and_allows_folder_crud()
    {
        var library = new StudioGraphFunctionLibrary(directory);
        var first = CreateFunction(1, "Append suffix");
        library.Add(first, "Text/Formatting");
        library.Add(CreateFunction(2, "Append marker"), "Text/Formatting");

        Assert.Throws<ArgumentException>(() => library.Add(first, "Other"));
        Assert.True(library.Move(first.FunctionId, first.Version, "Text/Legacy"));
        Assert.True(library.Delete(first.FunctionId, first.Version));

        var reopened = new StudioGraphFunctionLibrary(directory);
        var remaining = Assert.Single(reopened.List());
        Assert.Equal(2, remaining.Function.Definition.Version);
        Assert.Equal("Text/Formatting", remaining.Folder);
        Assert.Equal(GraphFunctionJson.ComputeHash(remaining.Function.Definition), remaining.Function.ContentHash);
    }

    [Fact]
    public void Library_rejects_invalid_function_bodies_and_preserves_embedded_snapshots_after_delete()
    {
        var library = new StudioGraphFunctionLibrary(directory);
        var function = CreateFunction(1, "Append suffix");
        var configuration = new GraphFunctionConfiguration(function, GraphFunctionJson.ComputeHash(function));
        var caller = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0") { GraphFunction = configuration };

        library.Add(function, "");
        Assert.Throws<ArgumentException>(() => library.Add(function with { Connections = function.Connections.Clear() }, ""));
        Assert.Throws<ArgumentNullException>(() => library.Add(CreateFunction(2, "Another revision"), null!));
        Assert.True(library.Delete(function.FunctionId, function.Version));
        Assert.Equal(configuration, caller.GraphFunction);
    }

    private static GraphFunctionDefinition CreateFunction(int version, string name) => new(
        "text.append-suffix", version, name, "Appends a fixed suffix to text.",
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
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
