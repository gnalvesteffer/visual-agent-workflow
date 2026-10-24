using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;

namespace WorkflowService;

internal static class DeploymentDependencies
{
    public static async Task<McpToolBroker> ResolveAsync(WorkflowRevision revision, bool grantDemoTool, IReadOnlyList<AgentProviderRegistration> providers, CancellationToken cancellationToken)
    {
        // This host registers one known tool fixture and only explicitly configured model providers. Artifacts cannot register code or grant tools.
        var policy = new ExplicitToolAuthorizationPolicy(grantDemoTool
            ? [new("service-mcp", "transform", "demo.reverse")] : []);
        var registrations = DemoMcpServer.ForCurrentExecutable().Where(r => r.BindingId == "demo.reverse").ToArray();
        var requirements = revision.Definition.Nodes
            .SelectMany(n => NodeToolRequirements.For(n.Value).Select(tool => (NodeId: n.Key, Tool: tool))).ToArray();
        if (revision.Definition.Nodes.Values.Where(n => n.Agent is not null).Any(n =>
            !providers.Any(p => p.Descriptor.ProviderId == n.Agent!.ProviderId && p.Descriptor.Version == n.Agent.ProviderVersion)))
            throw new InvalidOperationException("A pinned agent provider is unavailable; no substitution is permitted.");
        foreach (var requirement in requirements)
        {
            if (!registrations.Any(r => r.BindingId == requirement.Tool.BindingId))
                throw new InvalidOperationException("A required MCP binding is not registered in this host.");
            if (!policy.IsAllowed(revision, requirement.NodeId, requirement.Tool.BindingId)) throw new ToolAccessDeniedException();
        }
        var broker = new McpToolBroker(registrations, policy);
        if (requirements.Length == 0) return broker; // Pure workflows do not even discover MCP tools.
        var available = await broker.ListBindingsAsync(cancellationToken);
        foreach (var requirement in requirements)
            if (!available.Any(tool => tool.BindingId == requirement.Tool.BindingId && tool.ContractHash == requirement.Tool.ContractHash))
                throw new ToolContractMismatchException();
        return broker; // The broker repeats node-policy and contract checks on every call.
    }
}
