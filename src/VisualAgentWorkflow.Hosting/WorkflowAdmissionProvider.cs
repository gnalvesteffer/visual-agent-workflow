using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Host-owned, credential-free facts used for one run-start admission check.</summary>
public interface IWorkflowAdmissionSnapshotProvider
{
    Task<WorkflowAdmissionHostSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds an admission snapshot from the host's registered provider and MCP catalogs.</summary>
public sealed class CatalogWorkflowAdmissionSnapshotProvider(
    IAgentProviderCatalog providers,
    IToolBindingCatalog tools) : IWorkflowAdmissionSnapshotProvider
{
    public async Task<WorkflowAdmissionHostSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(tools);
        var providerIds = await providers.ListProvidersAsync().WaitAsync(cancellationToken);
        var bindings = await tools.ListBindingsAsync(cancellationToken);
        return new(
            providerIds.Select(provider => provider.ProviderId).ToImmutableArray(),
            bindings.Select(binding => new WorkflowAdmissionToolFact(binding.BindingId, binding.ContractHash)
            {
                DescriptionHash = binding.DescriptionHash
            }).ToImmutableArray())
        {
            ProviderFacts = providerIds.Select(provider => new WorkflowAdmissionProviderFact(provider.ProviderId, provider.ContractHash)).ToImmutableArray()
        };
    }
}
