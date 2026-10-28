using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioGraphTemplateLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-templates-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Catalog_add_reopen_move_and_exact_lookup_preserve_immutable_revision()
    {
        var template = IdentityTemplate();
        var library = new StudioGraphTemplateLibrary(directory);
        library.Add(template, "Text/Formatting");

        var reopened = new StudioGraphTemplateLibrary(directory);
        var entry = Assert.Single(reopened.List());
        Assert.Equal(template.TemplateId, entry.Template.Definition.TemplateId);
        Assert.Equal(GraphTemplateJson.ComputeHash(template), entry.Template.ContentHash);
        Assert.Equal(entry, reopened.Get(template.TemplateId, template.Version));
        Assert.True(reopened.Move(template.TemplateId, template.Version, "Archived"));
        Assert.Equal("Archived", Assert.Single(reopened.List()).Folder);
        Assert.Equal(entry.Template.ContentHash, Assert.Single(reopened.List()).Template.ContentHash);
    }

    [Fact]
    public void Catalog_rejects_duplicate_revisions_and_invalid_folders()
    {
        var template = IdentityTemplate();
        var library = new StudioGraphTemplateLibrary(directory);
        library.Add(template, "Text");

        Assert.Throws<ArgumentException>(() => library.Add(template, "Other"));
        Assert.Throws<ArgumentException>(() => library.Move(template.TemplateId, template.Version, "../escape"));
        Assert.False(library.Move("missing", 1, "Text"));
    }

    [Fact]
    public void Catalog_fails_closed_when_a_stored_hash_is_modified()
    {
        var template = IdentityTemplate();
        var library = new StudioGraphTemplateLibrary(directory);
        library.Add(template, "Text");
        var path = Path.Combine(directory, "graph-templates.json");
        var json = File.ReadAllText(path).Replace(GraphTemplateJson.ComputeHash(template), new string('0', 64), StringComparison.Ordinal);
        File.WriteAllText(path, json);

        Assert.Throws<InvalidDataException>(() => new StudioGraphTemplateLibrary(directory));
    }

    private static GraphTemplateDefinition IdentityTemplate() => new(
        "template.identity", 1, "Identity", "Passes text through.",
        new GraphFunctionDefinition(
            "template.identity.body", 1, "Identity", "Passes text through.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("value", new("Value", "string")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal).Add("result", new("Result", "string")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value" })
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new(new("input", "out"), new("output", "in"), "data"))));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
