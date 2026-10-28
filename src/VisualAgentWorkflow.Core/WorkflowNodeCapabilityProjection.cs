using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>How a node's in-memory capability view obtained its effective pin shape.</summary>
public enum WorkflowNodeCapabilityResolutionKind
{
    Static,
    Configured,
    DiscoveryEnriched
}

/// <summary>A non-serialized view of the capabilities already declared by one node.</summary>
/// <remarks>
/// This projection is not part of WorkflowDefinition, package content, content hashes, schema admission,
/// runtime authority, or host grants. It composes the canonical contract catalog with the existing
/// descriptor resolver. MCP discovery may enrich this view for authoring, but cannot add authority or
/// replace persisted pins.
/// </remarks>
public sealed record WorkflowNodeCapabilityProjection(
    string ProjectionVersion,
    WorkflowNodeContract Contract,
    WorkflowNodeDescriptor Descriptor,
    WorkflowNodeCapabilityResolutionKind ResolutionKind)
{
    public string NodeType => Contract.Type;
    public string NodeTypeVersion => Contract.TypeVersion;
    public string DescriptorVersion => Descriptor.DescriptorVersion;
    public string ConfigurationKind => Descriptor.ConfigurationKind;
    public ImmutableArray<WorkflowPortDescriptor> Ports => Descriptor.Ports;
    public ImmutableArray<WorkflowNodeLimit> Limits => Contract.Limits;
    public WorkflowNodeEffectFlags Effects => Contract.Effects;
    public WorkflowNodeAuthoringFlags Authoring => Contract.Authoring;
    public bool RequiresHostCapability => Contract.RequiresHostCapability;
}

/// <summary>Resolves the bounded, non-authoritative capability view for a configured node.</summary>
public static class WorkflowNodeCapabilityProjectionResolver
{
    public const string CurrentVersion = "1.0.0";

    public static bool TryResolve(
        NodeDefinition node,
        out WorkflowNodeCapabilityProjection projection,
        string projectionVersion = CurrentVersion,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        projection = null!;

        if (!string.Equals(projectionVersion, CurrentVersion, StringComparison.Ordinal) ||
            !WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) ||
            !WorkflowNodeDescriptorResolver.TryResolve(node, discoveredMcpInputPorts, discoveredMcpInputDescriptions, out var descriptor))
            return false;

        var hasDiscovery = node.Type == NodeTypes.McpTool &&
            (discoveredMcpInputPorts.HasValue || discoveredMcpInputDescriptions is not null);
        var resolutionKind = hasDiscovery
            ? WorkflowNodeCapabilityResolutionKind.DiscoveryEnriched
            : WorkflowNodeDescriptorRegistry.TryGetForNode(node, out _)
                ? WorkflowNodeCapabilityResolutionKind.Static
                : WorkflowNodeCapabilityResolutionKind.Configured;

        projection = new(projectionVersion, contract, descriptor, resolutionKind);
        return true;
    }

    public static WorkflowNodeCapabilityProjection Resolve(
        NodeDefinition node,
        string projectionVersion = CurrentVersion,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null) =>
        TryResolve(node, out var projection, projectionVersion, discoveredMcpInputPorts, discoveredMcpInputDescriptions)
            ? projection
            : throw new KeyNotFoundException($"No capability projection can be resolved for '{node.Type}@{projectionVersion}'.");
}
