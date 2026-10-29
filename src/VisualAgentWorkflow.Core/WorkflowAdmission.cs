using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Host-visible facts used for an advisory admission check. These facts contain no credentials or grants.</summary>
public sealed record WorkflowAdmissionHostSnapshot(
    ImmutableArray<string> ProviderIds,
    ImmutableArray<WorkflowAdmissionToolFact> Tools)
{
    public static WorkflowAdmissionHostSnapshot Empty { get; } = new([], []);
}

/// <summary>A discovered MCP binding identity used only for dependency comparison.</summary>
public sealed record WorkflowAdmissionToolFact(string BindingId, string ContractHash)
{
    public string? DescriptionHash { get; init; }
}

public enum WorkflowAdmissionIssueKind
{
    Missing,
    ContractMismatch,
    DescriptionMismatch
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
                case "agent-provider":
                    if (!providers.Contains(requirement.Id))
                        issues.Add(new(requirement, WorkflowAdmissionIssueKind.Missing,
                            $"The host does not advertise agent provider '{requirement.Id}'."));
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
            }
        }

        return new(package.Definition.WorkflowId, package.DefinitionHash, package.Requirements, issues.ToImmutable());
    }

    private static bool ValidBindingId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static bool ValidHash(string value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
