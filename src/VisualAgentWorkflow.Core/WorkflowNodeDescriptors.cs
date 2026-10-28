using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Versioned declarative pin metadata for a static node-family slice.</summary>
public sealed record WorkflowNodeDescriptor(
    string NodeType,
    string DescriptorVersion,
    string ConfigurationKind,
    ImmutableArray<WorkflowPortDescriptor> Ports)
{
    public WorkflowNodeContract NodeContract => WorkflowNodeContractCatalog.Get(NodeType);
}

/// <summary>
/// Exact descriptors for nodes whose pin shape is independent of node configuration.
/// Dynamic/configuration-derived nodes remain on their specialized resolvers.
/// </summary>
public static class WorkflowNodeDescriptorRegistry
{
    public const string CurrentVersion = "1.0.0";

    public static ImmutableArray<WorkflowNodeDescriptor> All { get; } =
    [
        Descriptor(NodeTypes.StringConstant, "string-constant", [Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.BooleanConstant, "boolean-constant", [Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.IntegerConstant, "integer-constant", [Output(WorkflowDataTypes.Integer)]),
        Descriptor(NodeTypes.NumberConstant, "number-constant", [Output(WorkflowDataTypes.Number)]),
        Descriptor(NodeTypes.NumberAdd, "number-add", [Input("left", WorkflowDataTypes.Number), Input("right", WorkflowDataTypes.Number), Output(WorkflowDataTypes.Number)]),
        Descriptor(NodeTypes.TextConcat, "text-concat", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.StringEquals, "string-equals", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.IntegerAdd, "integer-add", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Integer)]),
        Descriptor(NodeTypes.IntegerEquals, "integer-equals", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.BooleanNot, "boolean-not", [Input("value", WorkflowDataTypes.Boolean), Output(WorkflowDataTypes.Boolean)])
    ];

    public static bool TryGet(string? nodeType, string descriptorVersion, out WorkflowNodeDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeType, nodeType, StringComparison.Ordinal) &&
            string.Equals(candidate.DescriptorVersion, descriptorVersion, StringComparison.Ordinal))!;
        return descriptor is not null;
    }

    public static WorkflowNodeDescriptor Get(string nodeType, string descriptorVersion = CurrentVersion) =>
        TryGet(nodeType, descriptorVersion, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"No workflow node descriptor '{nodeType}@{descriptorVersion}' is registered.");

    private static WorkflowNodeDescriptor Descriptor(string nodeType, string configurationKind,
        ImmutableArray<WorkflowPortDescriptor> ports) =>
        new(nodeType, CurrentVersion, configurationKind, ports);

    private static WorkflowPortDescriptor Input(string id, string dataType) =>
        new(id, WorkflowPortDirection.Input, WorkflowPortKind.Data, dataType)
        {
            Required = true,
            Cardinality = WorkflowPortCardinality.ExactlyOne
        };

    private static WorkflowPortDescriptor Output(string dataType) =>
        new("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, dataType)
        {
            Cardinality = WorkflowPortCardinality.Many
        };
}

/// <summary>Projects the effective pin contract for a configured node without changing the node definition.</summary>
/// <remarks>
/// This is a shape-only projection. Schema admission, validation, persistence, runtime execution, and host authority
/// remain owned by their existing boundaries. Live MCP metadata is optional authoring input and is never persisted.
/// </remarks>
public static class WorkflowNodeDescriptorResolver
{
    public static bool TryResolve(NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions,
        out WorkflowNodeDescriptor descriptor)
    {
        if (!WorkflowNodeContractCatalog.TryGet(node.Type, out _))
        {
            descriptor = null!;
            return false;
        }

        if (WorkflowNodeDescriptorRegistry.TryGet(node.Type, WorkflowNodeDescriptorRegistry.CurrentVersion, out descriptor))
            return true;

        var effectiveNode = node.Type == NodeTypes.McpTool && discoveredMcpInputPorts.HasValue && node.McpInputPorts is null
            ? node with { McpInputPorts = discoveredMcpInputPorts.Value }
            : node;
        var ports = new List<WorkflowPortDescriptor>();
        var mcpInputs = effectiveNode.McpInputPorts;

        foreach (var port in WorkflowPorts.ExplicitDataInputs(effectiveNode))
        {
            var mcpPort = effectiveNode.Type == NodeTypes.McpTool
                ? mcpInputs?.FirstOrDefault(candidate => candidate.Id == port.Id)
                : null;
            ports.Add(new WorkflowPortDescriptor(port.Id, WorkflowPortDirection.Input, WorkflowPortKind.Data, port.DataType)
            {
                Description = effectiveNode.Type == NodeTypes.McpTool && discoveredMcpInputDescriptions is not null &&
                    discoveredMcpInputDescriptions.TryGetValue(port.Id, out var description) ? description : null,
                Required = effectiveNode.Type != NodeTypes.McpTool || mcpInputs is not { } || mcpPort?.Required == true,
                Cardinality = effectiveNode.Type == NodeTypes.McpTool
                    ? WorkflowPortCardinality.AtMostOne
                    : WorkflowPortCardinality.ExactlyOne,
                SupportsStaticValue = effectiveNode.Type == NodeTypes.McpTool
            });
        }

        foreach (var port in WorkflowPorts.ExplicitDataOutputs(effectiveNode))
            ports.Add(new WorkflowPortDescriptor(port.Id, WorkflowPortDirection.Output, WorkflowPortKind.Data, port.DataType)
            {
                Cardinality = WorkflowPortCardinality.Many
            });

        foreach (var port in WorkflowPorts.ExplicitExecutionInputs(effectiveNode))
            ports.Add(new WorkflowPortDescriptor(port, WorkflowPortDirection.Input, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            });

        foreach (var port in WorkflowPorts.ExplicitExecutionOutputs(effectiveNode))
            ports.Add(new WorkflowPortDescriptor(port, WorkflowPortDirection.Output, WorkflowPortKind.Execution,
                DisplayName: ExecutionPortLabel(effectiveNode, port))
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            });

        descriptor = new WorkflowNodeDescriptor(effectiveNode.Type, WorkflowNodeDescriptorRegistry.CurrentVersion,
            ConfigurationKind(effectiveNode.Type), [.. ports]);
        return true;
    }

    public static WorkflowNodeDescriptor Resolve(NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null) =>
        TryResolve(node, discoveredMcpInputPorts, discoveredMcpInputDescriptions, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"No workflow node descriptor can be resolved for '{node.Type}'.");

    private static string? ExecutionPortLabel(NodeDefinition node, string port) =>
        node.Type == NodeTypes.Agent
            ? node.Agent?.Routes?.FirstOrDefault(route => route is not null && AgentExecutionRouteLimits.PinId(route.Id) == port)?.Label
            : node.Type == NodeTypes.ParallelFork
                ? node.ParallelFork?.Branches.FirstOrDefault(branch => branch is not null && ParallelBranchLimits.PinId(branch.Id) == port)?.Label
                : node.Type == NodeTypes.Sequence
                    ? node.Sequence?.Outputs.FirstOrDefault(output => output is not null && SequenceOutputLimits.PinId(output.Id) == port)?.Label
                    : null;

    private static string ConfigurationKind(string nodeType) => nodeType switch
    {
        NodeTypes.McpTool => "mcp-tool",
        NodeTypes.Agent => "agent",
        NodeTypes.FunctionCall => "graph-function-call",
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous => "parallel",
        NodeTypes.Sequence => "sequence",
        _ => "configured"
    };
}
