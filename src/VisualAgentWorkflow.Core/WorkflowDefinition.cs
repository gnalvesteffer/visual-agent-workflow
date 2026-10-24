using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Portable definition. UI layout and environment bindings are stored separately.</summary>
public sealed record WorkflowDefinition(
    string SchemaVersion,
    string WorkflowId,
    string EntryNodeId,
    ImmutableSortedDictionary<string, NodeDefinition> Nodes,
    ImmutableSortedDictionary<string, ConnectionDefinition> Connections);

public sealed record NodeDefinition(string Type, string TypeVersion);
public sealed record PortReference(string NodeId, string PortId);
public sealed record ConnectionDefinition(PortReference Source, PortReference Target, string Kind);

/// <summary>The initial execution subset is a linear string pipeline, not a general graph engine.</summary>
public static class NodeTypes
{
    public const string Input = "core.input";
    public const string Uppercase = "core.text.uppercase";
    public const string Reverse = "core.text.reverse";
    public const string Output = "core.output";

    public static bool IsSupported(string? type) =>
        type is Input or Uppercase or Reverse or Output;
}
