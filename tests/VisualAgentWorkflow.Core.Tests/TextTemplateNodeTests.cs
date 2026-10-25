using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class TextTemplateNodeTests
{
    private static WorkflowDefinition Definition(string workflowId = "text-template", PromptTemplate? template = null) =>
        new WorkflowDefinition("1.7", workflowId, "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("format", new(NodeTypes.TextTemplate, "1.0.0")
                {
                    TextTemplate = template ?? new([new("literal", "Hello "), new("input", ""), new("literal", " "), new("parameter", "name")])
                })
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-format", new(new("input", "out"), new("format", "in"), "data"))
                .Add("format-output", new(new("format", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("name", new("Name", "Ada"))
        };

    [Fact]
    public void TextTemplateRoundTripsAndDeclaresItsNodeRequirement()
    {
        var definition = Definition();
        var json = WorkflowJson.Serialize(definition);
        var restored = WorkflowJson.Deserialize(json);
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(restored, WorkflowLayout.Create(restored)));

        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal(definition.Nodes["format"].TextTemplate!.Parts.Select(part => (part.Kind, part.Value)),
            restored.Nodes["format"].TextTemplate!.Parts.Select(part => (part.Kind, part.Value)));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored));
        Assert.Contains(new PackageRequirement("node", NodeTypes.TextTemplate, "1.0.0"), package.Requirements);
    }

    [Fact]
    public void InsertingTextTemplateUpgradesOldWorkflowToItsMinimumSchema()
    {
        var source = new WorkflowDefinition("1.0", "template-insert", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0")).Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("edge", new(new("input", "out"), new("output", "in"), "data")));

        var inserted = WorkflowEditing.InsertNodeAfter(source, "input",
            new(NodeTypes.TextTemplate, "1.0.0") { TextTemplate = new([new("input", "")]) });

        Assert.Equal("1.7", inserted.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(inserted));
        Assert.Contains(inserted.Nodes.Values, node => node.Type == NodeTypes.TextTemplate);
    }

    [Fact]
    public void TextTemplateReferencesMustBeBoundedAndDeclared()
    {
        var definition = Definition();
        var node = definition.Nodes["format"];
        var invalidTemplates = new[]
        {
            new PromptTemplate([new("parameter", "missing")]),
            new PromptTemplate([new("expression", "1 + 1")]),
            new PromptTemplate(Enumerable.Repeat(new PromptPart("literal", "x"), 65).ToImmutableArray()),
            new PromptTemplate([new("literal", new string('x', 8193))]),
            new PromptTemplate([new("literal", new string('x', 4097)), new("literal", new string('y', 4096))])
        };

        foreach (var template in invalidTemplates)
            Assert.Contains(WorkflowValidator.Validate(definition with
            {
                Nodes = definition.Nodes.SetItem("format", node with { TextTemplate = template })
            }), issue => issue.Code == "text.template");

        Assert.Contains(WorkflowValidator.Validate(definition with { SchemaVersion = "1.6" }), issue => issue.Code == "node.schema");
        Assert.Contains(WorkflowValidator.Validate(definition with
        {
            Nodes = definition.Nodes.SetItem("input", definition.Nodes["input"] with { TextTemplate = node.TextTemplate })
        }), issue => issue.Code == "text.template.unexpected");
    }

    [Fact]
    public void ReferencedParameterCannotBeRemoved()
    {
        Assert.Throws<ArgumentException>(() => WorkflowParameterEditing.Remove(Definition(), "name"));
    }

    [Fact]
    public void ReusableInsertionCopiesAndRemapsTemplateParameterReferences()
    {
        var source = Definition("template-source");
        var revision = WorkflowRevision.Create(source, "test");
        var target = new WorkflowDefinition("1.7", "template-target", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("step", new(NodeTypes.Uppercase, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("a", new(new("input", "out"), new("step", "in"), "data"))
                .Add("b", new(new("step", "out"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("name", new("Name", "Grace"))
        };

        var composition = WorkflowReuse.Insert(target, WorkflowLayout.Create(target), "input",
            new(1, revision), WorkflowLayout.Create(source));
        var copied = composition.Definition.Nodes.Values.Single(node => node.Type == NodeTypes.TextTemplate);
        var parameterReference = Assert.Single(copied.TextTemplate!.Parts.Where(part => part.Kind == "parameter"));

        Assert.NotEqual("name", parameterReference.Value);
        Assert.Equal("Ada", composition.Definition.Parameters![parameterReference.Value].DefaultValue);
        Assert.Empty(WorkflowValidator.Validate(composition.Definition));
    }
}
