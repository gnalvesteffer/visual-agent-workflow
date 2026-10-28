using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowAdmissionTests
{
    private const string ContractHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DescriptionHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Exact_provider_and_tool_snapshot_satisfies_external_requirements()
    {
        var definition = Definition();
        var report = WorkflowAdmission.Assess(definition, new(
            ["demo.scripted"],
            [new("memory.search", ContractHash) { DescriptionHash = DescriptionHash }]));

        Assert.True(report.HostDependenciesSatisfied);
        Assert.Equal(definition.WorkflowId, report.WorkflowId);
        Assert.Contains(report.Requirements, requirement => requirement.Kind == "agent-provider");
        Assert.Contains(report.Requirements, requirement => requirement.Kind == "mcp-tool");
    }

    [Fact]
    public void Missing_and_mismatched_host_dependencies_are_distinguished()
    {
        var definition = Definition();

        var missing = WorkflowAdmission.Assess(definition, WorkflowAdmissionHostSnapshot.Empty);
        Assert.Contains(missing.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.Missing && issue.Requirement.Kind == "agent-provider");
        Assert.Contains(missing.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.Missing && issue.Requirement.Kind == "mcp-tool");

        var mismatched = WorkflowAdmission.Assess(definition, new(
            ["demo.scripted"],
            [new("memory.search", new string('c', 64)) { DescriptionHash = new string('d', 64) }]));
        Assert.Contains(mismatched.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.ContractMismatch);
        Assert.DoesNotContain(mismatched.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.DescriptionMismatch);

        var descriptionMismatch = WorkflowAdmission.Assess(definition, new(
            ["demo.scripted"],
            [new("memory.search", ContractHash) { DescriptionHash = new string('d', 64) }]));
        Assert.Contains(descriptionMismatch.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.DescriptionMismatch);
    }

    [Fact]
    public void Embedded_requirements_are_not_reported_as_host_missing_and_results_are_stable()
    {
        var definition = Definition() with
        {
            SchemaVersion = "1.7",
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add(
                "research@1.0", WorkflowSkill.Create("research", "1.0", "Research", "Use evidence."))
        };

        var first = WorkflowAdmission.Assess(definition, WorkflowAdmissionHostSnapshot.Empty);
        var second = WorkflowAdmission.Assess(definition, WorkflowAdmissionHostSnapshot.Empty);

        Assert.Equal(first.WorkflowId, second.WorkflowId);
        Assert.Equal(first.DefinitionHash, second.DefinitionHash);
        Assert.Equal(first.Requirements.Select(requirement => (requirement.Kind, requirement.Id, requirement.Version,
                requirement.ContractHash, requirement.DescriptionHash)),
            second.Requirements.Select(requirement => (requirement.Kind, requirement.Id, requirement.Version,
                requirement.ContractHash, requirement.DescriptionHash)));
        Assert.Equal(first.Issues.Select(issue => (issue.Requirement, issue.Kind, issue.Message)),
            second.Issues.Select(issue => (issue.Requirement, issue.Kind, issue.Message)));
        Assert.DoesNotContain(first.Issues, issue => issue.Requirement.Kind is "skill" or "node");
    }

    [Fact]
    public void Fabricated_package_requirements_are_rejected_before_assessment()
    {
        var definition = Definition();
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition)) with
        {
            Requirements = []
        };

        Assert.Throws<ArgumentException>(() => WorkflowAdmission.Assess(package, WorkflowAdmissionHostSnapshot.Empty));
    }

    private static WorkflowDefinition Definition()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new(
            "demo.scripted", null, "Use the tool.",
            [new ToolBindingRequest("memory.search", ContractHash) { DescriptionHash = DescriptionHash }]));
        return new WorkflowDefinition("1.2", "admission-test", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["agent"] = agent,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["first"] = new(new("input", "out"), new("agent", "in"), "data"),
                ["last"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
    }
}
