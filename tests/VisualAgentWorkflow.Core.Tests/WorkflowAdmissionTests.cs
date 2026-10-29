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
    public void Sequence_agent_path_package_requires_the_terminal_agent_provider()
    {
        var definition = SequenceAgentPathDefinition();
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));

        var requirements = package.Requirements;
        var provider = Assert.Single(requirements, requirement => requirement.Kind == "agent-provider-contract");

        Assert.Equal("sequence-proof", provider.Id);
        var host = new WorkflowAdmissionHostSnapshot([provider.Id],
            [new WorkflowAdmissionToolFact("demo.inventory", new string('a', 64))])
        {
            ProviderFacts = [new WorkflowAdmissionProviderFact(provider.Id, provider.ContractHash!)],
            AgentGrants = [new WorkflowAdmissionAgentGrant(definition.WorkflowId, package.DefinitionHash,
                "research-agent", provider.Id, provider.ContractHash!)]
        };
        Assert.True(WorkflowAdmission.Assess(package, host).HostDependenciesSatisfied);
        Assert.Contains(WorkflowAdmission.Assess(definition, WorkflowAdmissionHostSnapshot.Empty).Issues,
            issue => issue.Kind == WorkflowAdmissionIssueKind.Missing && issue.Requirement == provider);
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

    [Fact]
    public void Malformed_or_ambiguous_host_facts_are_rejected_before_comparison()
    {
        var definition = Definition();

        Assert.Throws<ArgumentException>(() => WorkflowAdmission.Assess(definition, new(
            ["demo.scripted", "demo.scripted"],
            [new("memory.search", ContractHash)])));
        Assert.Throws<ArgumentException>(() => WorkflowAdmission.Assess(definition, new(
            ["demo.scripted"],
            [new("memory.search", "not-a-contract-hash")])));
        Assert.Throws<ArgumentException>(() => WorkflowAdmission.Assess(definition, new(
            ["demo.scripted"],
            [new("memory.search", ContractHash) { DescriptionHash = "not-a-description-hash" }])));
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

    private static WorkflowDefinition SequenceAgentPathDefinition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("inventory", "Inventory"), new("research", "Research")])
            },
            ["inventory-tool"] = new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("demo.inventory", new string('a', 64))) { McpArgumentsJson = "{}" },
            ["research-agent"] = new(NodeTypes.Agent, "1.0.0")
            {
                Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
                { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-inventory"] = new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"),
            ["inventory-output"] = new(new("inventory-tool", "then"), new("output", "execute"), "execution"),
            ["sequence-research"] = new(new("sequence", "then.research"), new("research-agent", "execute"), "execution"),
            ["research-output"] = new(new("research-agent", "out"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceAgentPathGraph, "sequence-admission", "input",
            nodes.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }
}
