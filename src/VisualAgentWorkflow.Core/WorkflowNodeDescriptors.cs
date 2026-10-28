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
