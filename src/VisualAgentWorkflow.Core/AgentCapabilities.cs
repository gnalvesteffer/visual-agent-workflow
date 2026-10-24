using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record AgentConfiguration(string ProviderId, string ProviderVersion, string Instructions, ImmutableArray<ToolBindingRequest> Tools);
public sealed record AgentProviderDescriptor(string ProviderId, string Version, string DisplayName, string ModelId, bool IsDeterministic);

public interface IAgentProviderCatalog
{
    Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync();
}

public interface IAgentNodeExecutor : IAgentProviderCatalog
{
    Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default);
}

public static class NodeToolRequirements
{
    public static IReadOnlyList<ToolBindingRequest> For(NodeDefinition node) => node.Agent is { } agent
        ? agent.Tools : node.Tool is { } tool ? [tool] : [];
}
