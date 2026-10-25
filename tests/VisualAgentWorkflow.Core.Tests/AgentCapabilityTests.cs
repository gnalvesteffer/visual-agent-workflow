using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class AgentCapabilityTests
{
    private static WorkflowDefinition Definition(WorkflowSkill skill)
    {
        var draft = new WorkflowDefinition("1.2", "prompt-test", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["step-1"] = new(NodeTypes.Agent, "1.0.0", Agent: new("demo.scripted", "1.0.0", "Answer briefly.", [])),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["first"] = new(new("input", "out"), new("step-1", "in"), "data"),
                ["last"] = new(new("step-1", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var node = draft.Nodes["step-1"];
        return draft with
        {
            SchemaVersion = "1.7",
            Nodes = draft.Nodes.SetItem("step-1", node with { Agent = node.Agent! with
            {
                Skills = [new(skill.Id, skill.Version, skill.ContentHash)],
                UserMessage = new([new("literal", "Question: "), new("input", ""), new("literal", " Context: "), new("parameter", "context")])
            } }),
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("context", new("Context", "local")),
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add(skill.Id + "@" + skill.Version, skill)
        };
    }

    [Fact]
    public void EmbeddedSkillAndPromptSurvivePackageRoundTrip()
    {
        var skill = WorkflowSkill.Create("math", "1.0", "Arithmetic", "Return a single number.");
        var definition = Definition(skill);
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition)));
        Assert.Equal(skill, package.Definition.Skills!["math@1.0"]);
        Assert.Contains(package.Requirements, requirement => requirement.Kind == "skill" && requirement.ContractHash == skill.ContentHash);
        Assert.Equal("Question: 1+1? Context: local", PromptRendering.Render(
            package.Definition.Nodes["step-1"].Agent!.UserMessage!, "1+1?", new Dictionary<string, string> { ["context"] = "local" }));
    }

    [Fact]
    public void UnresolvedSkillOrPromptReferenceCannotBePublished()
    {
        var skill = WorkflowSkill.Create("math", "1.0", "Arithmetic", "Return a single number.");
        var definition = Definition(skill);
        Assert.Contains(WorkflowValidator.Validate(definition with { Skills = null }), issue => issue.Code == "agent.skills");
        var node = definition.Nodes["step-1"];
        var missingParameter = definition with { Nodes = definition.Nodes.SetItem("step-1", node with
        { Agent = node.Agent! with { UserMessage = new([new("parameter", "missing")]) } }) };
        Assert.Contains(WorkflowValidator.Validate(missingParameter), issue => issue.Code == "agent.prompt");
    }

    [Fact]
    public void RenderingRejectsMissingAndOversizedValues()
    {
        Assert.Throws<InvalidOperationException>(() => PromptRendering.Render(new([new("parameter", "missing")]), "", null));
        Assert.Throws<InvalidOperationException>(() => PromptRendering.Render(new([new("input", ""), new("input", "")]),
            new string('x', 9000), null));
    }

    [Fact]
    public void PromptTemplateCodecPreservesReferencesAndEscapesLiteralTokens()
    {
        var parameters = new Dictionary<string, WorkflowParameterDefinition>
        {
            ["customer"] = new("Customer", "Ada")
        };
        var expected = new PromptTemplate([
            new("literal", "Say "), new("input", ""), new("literal", " to "),
            new("parameter", "customer"), new("literal", "; literal {{input}} and slash \\")]);

        var formatted = PromptTemplateCodec.Format(expected);
        Assert.Equal("Say {{input}} to {{parameter:customer}}; literal \\{{input}} and slash \\\\", formatted);
        var parsed = PromptTemplateCodec.Parse(formatted, parameters);
        Assert.Equal(expected.Parts.ToArray(), parsed.Parts.ToArray());
        Assert.Throws<FormatException>(() => PromptTemplateCodec.Parse("{{missing}}", parameters));
        Assert.Throws<FormatException>(() => PromptTemplateCodec.Parse("Start {{", parameters));
    }

    [Fact]
    public void ToolDescriptionPinSurvivesWorkflowPackageExport()
    {
        var skill = WorkflowSkill.Create("math", "1.0", "Arithmetic", "Return a single number.");
        var definition = Definition(skill);
        var original = definition.Nodes["step-1"];
        var descriptionHash = new string('a', 64);
        definition = definition with { Nodes = definition.Nodes.SetItem("step-1", original with
        {
            Agent = original.Agent! with { Tools = [new("tool.add", new string('b', 64)) { DescriptionHash = descriptionHash }] }
        }) };

        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition)));

        Assert.Equal(descriptionHash, package.Definition.Nodes["step-1"].Agent!.Tools[0].DescriptionHash);
        Assert.Equal(descriptionHash, Assert.Single(package.Requirements, requirement => requirement.Kind == "mcp-tool").DescriptionHash);
    }

    [Fact]
    public void TypedParametersValidateDefaultsAndOverridesAndRoundTripAsSchema18()
    {
        var definition = new WorkflowDefinition("1.8", "typed-parameters", "input",
            new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition> { ["link"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("attempts", new("Attempts", "3") { Type = "integer" })
                .Add("mode", new("Reasoning", "default") { Type = "choice", Choices = ["default", "low", "medium", "high"] })
        };

        var package = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        Assert.Equal("integer", package.Parameters!["attempts"].Type);
        var renamed = WorkflowParameterEditing.Update(package, "attempts", "Max attempts", "3");
        Assert.Equal("integer", renamed.Parameters!["attempts"].Type);
        Assert.Equal("3", WorkflowParameters.Resolve(package, new Dictionary<string, string> { ["attempts"] = "03", ["mode"] = "high" })!["attempts"]);
        Assert.Throws<ArgumentException>(() => WorkflowParameters.Resolve(package, new Dictionary<string, string> { ["attempts"] = "3.14" }));
        Assert.Throws<ArgumentException>(() => WorkflowParameters.Resolve(package, new Dictionary<string, string> { ["mode"] = "unlisted" }));
        Assert.Equal("high", WorkflowParameters.Resolve(package, new Dictionary<string, string> { ["mode"] = "high" })!["mode"]);
    }
}
