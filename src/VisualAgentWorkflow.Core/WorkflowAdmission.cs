using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Host-visible facts used for an advisory admission check. These facts contain no credentials or grants.</summary>
public sealed record WorkflowAdmissionHostSnapshot(
    ImmutableArray<string> ProviderIds,
    ImmutableArray<WorkflowAdmissionToolFact> Tools)
{
    /// <summary>Exact immutable provider contracts discovered by the trusted host.</summary>
    public ImmutableArray<WorkflowAdmissionProviderFact> ProviderFacts { get; init; } = [];
    /// <summary>Host-owned grants bound to one workflow content identity and terminal Agent node.</summary>
    public ImmutableArray<WorkflowAdmissionAgentGrant> AgentGrants { get; init; } = [];
    public static WorkflowAdmissionHostSnapshot Empty { get; } = new([], []);
}

public sealed record WorkflowAdmissionProviderFact(string ProviderId, string ContractHash);

public sealed record WorkflowAdmissionAgentGrant(
    string WorkflowId, string DefinitionHash, string NodeId, string ProviderId, string ContractHash);

/// <summary>A discovered MCP binding identity used only for dependency comparison.</summary>
public sealed record WorkflowAdmissionToolFact(string BindingId, string ContractHash)
{
    public string? DescriptionHash { get; init; }
}

public enum WorkflowAdmissionIssueKind
{
    Missing,
    ContractMismatch,
    DescriptionMismatch,
    GrantDenied,
    UnknownRequirement
}

public sealed record WorkflowAdmissionIssue(
    PackageRequirement Requirement,
    WorkflowAdmissionIssueKind Kind,
    string Message);

/// <summary>
/// Deterministic, advisory evidence about whether a host snapshot contains the external dependencies
/// declared by one immutable workflow package. It is not an authorization decision.
/// </summary>
public sealed record WorkflowAdmissionReport(
    string WorkflowId,
    string DefinitionHash,
    ImmutableArray<PackageRequirement> Requirements,
    ImmutableArray<WorkflowAdmissionIssue> Issues)
{
    public bool HostDependenciesSatisfied => Issues.IsEmpty;
}

/// <summary>
/// Compares persisted package requirements with a host-owned snapshot without performing discovery,
/// granting access, or replacing invocation-time broker/policy checks.
/// </summary>
public static class WorkflowAdmission
{
    public static WorkflowAdmissionReport Assess(
        WorkflowDefinition definition,
        WorkflowAdmissionHostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        return Assess(package, host);
    }

    public static WorkflowAdmissionReport Assess(
        WorkflowPackage package,
        WorkflowAdmissionHostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);

        // A caller cannot use a fabricated package requirement list to bypass the definition boundary.
        var expected = WorkflowPackageJson.Create(package.Definition, package.Layout);
        if (!string.Equals(expected.DefinitionHash, package.DefinitionHash, StringComparison.Ordinal) ||
            !string.Equals(expected.LayoutHash, package.LayoutHash, StringComparison.Ordinal) ||
            !expected.Requirements.SequenceEqual(package.Requirements))
            throw new ArgumentException("The workflow package identity or requirements do not match its contents.", nameof(package));

        if (host.ProviderIds.Any(provider => string.IsNullOrWhiteSpace(provider) || provider.Length > 128 || provider.Any(char.IsControl)) ||
            host.ProviderIds.Distinct(StringComparer.Ordinal).Count() != host.ProviderIds.Length)
            throw new ArgumentException("The host snapshot must contain unique, bounded provider IDs.", nameof(host));
        var providers = host.ProviderIds.ToImmutableHashSet(StringComparer.Ordinal);
        if (host.ProviderFacts.Any(fact => fact is null || !ValidBindingId(fact.ProviderId) || !ValidHash(fact.ContractHash)) ||
            host.ProviderFacts.Select(fact => fact.ProviderId).Distinct(StringComparer.Ordinal).Count() != host.ProviderFacts.Length)
            throw new ArgumentException("The host snapshot must contain unique, bounded provider contract facts.", nameof(host));
        var providerFacts = host.ProviderFacts.ToDictionary(fact => fact.ProviderId, StringComparer.Ordinal);
        if (host.AgentGrants.Any(grant => grant is null || !ValidBindingId(grant.WorkflowId) || !ValidHash(grant.DefinitionHash) ||
            !ValidBindingId(grant.NodeId) || !ValidBindingId(grant.ProviderId) || !ValidHash(grant.ContractHash)) ||
            host.AgentGrants.Distinct().Count() != host.AgentGrants.Length)
            throw new ArgumentException("The host snapshot must contain unique, bounded Agent grants.", nameof(host));
        if (host.Tools.Any(tool => tool is null || !ValidBindingId(tool.BindingId) || !ValidHash(tool.ContractHash) ||
            tool.DescriptionHash is not null && !ValidHash(tool.DescriptionHash)))
            throw new ArgumentException("The host snapshot must contain bounded binding IDs and valid contract hashes.", nameof(host));
        if (host.Tools.GroupBy(tool => tool.BindingId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("The host snapshot must contain at most one fact per MCP binding.", nameof(host));
        var tools = host.Tools
            .ToDictionary(tool => tool.BindingId, StringComparer.Ordinal);
        var issues = ImmutableArray.CreateBuilder<WorkflowAdmissionIssue>();

        foreach (var requirement in package.Requirements)
        {
            switch (requirement.Kind)
            {
                case "node":
                case "workflow-revision":
                case "graph-function":
                case "skill":
                case "agent-profile":
                case "graph-function-tool":
                    // Portable package contents are validated by WorkflowPackageJson; these are not host dependencies.
                    break;
                case "agent-provider":
                    if (!providers.Contains(requirement.Id))
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.Missing,
                            $"The host does not advertise agent provider '{requirement.Id}'."));
                    break;
                case "agent-provider-contract":
                    if (!providerFacts.TryGetValue(requirement.Id, out var providerFact))
                    {
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.Missing,
                            $"The host does not advertise provider contract '{requirement.Id}'."));
                        break;
                    }
                    if (!string.Equals(requirement.ContractHash, providerFact.ContractHash, StringComparison.Ordinal))
                    {
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.ContractMismatch,
                            $"Agent provider '{requirement.Id}' has a different execution contract."));
                        break;
                    }
                    foreach (var node in package.Definition.Nodes.Where(pair =>
                                 pair.Value.Agent is { ProviderId: var providerId, ProviderContractHash: var contractHash } &&
                                 providerId == requirement.Id && contractHash == requirement.ContractHash))
                    {
                        if (!host.AgentGrants.Contains(new(package.Definition.WorkflowId, package.DefinitionHash,
                                node.Key, requirement.Id, requirement.ContractHash!),
                                EqualityComparer<WorkflowAdmissionAgentGrant>.Default))
                            issues.Add(new(requirement, WorkflowAdmissionIssueKind.GrantDenied,
                                $"The host did not grant provider contract '{requirement.Id}' for Agent node '{node.Key}'."));
                    }
                    break;
                case "mcp-tool":
                    if (!tools.TryGetValue(requirement.Id, out var tool))
                    {
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.Missing,
                            $"The host does not advertise MCP binding '{requirement.Id}'."));
                        break;
                    }
                    if (!string.Equals(requirement.ContractHash, tool.ContractHash, StringComparison.Ordinal))
                    {
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.ContractMismatch,
                            $"MCP binding '{requirement.Id}' has a different contract hash."));
                        break;
                    }
                    if (requirement.DescriptionHash is not null &&
                        !string.Equals(requirement.DescriptionHash, tool.DescriptionHash, StringComparison.Ordinal))
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.DescriptionMismatch,
                            $"MCP binding '{requirement.Id}' has a different description hash."));
                    break;
                default:
                    issues.Add(new(requirement, WorkflowAdmissionIssueKind.UnknownRequirement,
                        $"The host cannot evaluate workflow requirement kind '{requirement.Kind}'."));
                    break;
            }
        }

        return new(package.Definition.WorkflowId, package.DefinitionHash, package.Requirements, issues.ToImmutable());
    }

    private static bool ValidBindingId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static bool ValidHash(string value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
