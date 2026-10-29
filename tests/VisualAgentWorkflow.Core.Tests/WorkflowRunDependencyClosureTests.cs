using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowRunDependencyClosureTests
{
    [Fact]
    public void Create_is_deterministic_and_deduplicates_embedded_reusable_assets()
    {
        var skill = WorkflowSkill.Create("research", "1.0.0", "Research", "Use evidence.");
        var profile = AgentProfile.Create("review", "1.0.0", "Review", "Review facts.", "provider", "Review.", [], [skill]);
        var definition = Definition() with
        {
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add("research@1.0.0", skill),
            AgentProfiles = ImmutableSortedDictionary<string, AgentProfile>.Empty.Add("review@1.0.0", profile)
        };

        var first = WorkflowRunDependencyClosure.Create(definition);
        var second = WorkflowRunDependencyClosure.Create(definition);

        Assert.Equal(first.ClosureHash, second.ClosureHash);
        Assert.True(first.Dependencies.SequenceEqual(second.Dependencies));
        Assert.Equal(2, first.Dependencies.Length);
        Assert.Contains(first.Dependencies, dependency => dependency.Kind == "skill" && dependency.ContentHash == skill.ContentHash);
        Assert.Contains(first.Dependencies, dependency => dependency.Kind == "agent-profile" && dependency.Id == "review");
        WorkflowRunDependencyClosure.Validate(first);
    }

    [Fact]
    public void Validate_rejects_tampered_closure_hash()
    {
        var closure = WorkflowRunDependencyClosure.Create(Definition());

        Assert.Throws<InvalidDataException>(() => WorkflowRunDependencyClosure.Validate(
            closure with { ClosureHash = new string('a', 64) }));
    }

    private static WorkflowDefinition Definition() => new(
        "1.11", "closure-proof", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("link", new(new("input", "out"), new("output", "in"), "data")));
}
