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
}
