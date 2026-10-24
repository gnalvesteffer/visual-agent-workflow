using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Portable definition. UI layout and environment bindings are stored separately.</summary>
public sealed record WorkflowDefinition(
    string SchemaVersion,
    string WorkflowId,
    string EntryNodeId,
    ImmutableSortedDictionary<string, NodeDefinition> Nodes,
    ImmutableSortedDictionary<string, ConnectionDefinition> Connections);

public sealed record NodeDefinition(string Type, string TypeVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolBindingRequest? Tool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentConfiguration? Agent = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeOrigin? Origin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringCondition? Condition { get; init; }
}

/// <summary>Copy-on-insert source identity; informational provenance, never a grant or runtime resolver.</summary>
public sealed record NodeOrigin(string WorkflowId, Guid RevisionId, int Version, string ContentHash, string NodeId);
public sealed record PortReference(string NodeId, string PortId);
public sealed record ConnectionDefinition(PortReference Source, PortReference Target, string Kind);

/// <summary>The execution subset is an acyclic single-path string graph; parallelism and loops are unsupported.</summary>
public static class NodeTypes
{
    public const string Input = "core.input";
    public const string Uppercase = "core.text.uppercase";
    public const string Reverse = "core.text.reverse";
    public const string Output = "core.output";
    public const string McpTool = "core.mcp-tool";
    public const string Agent = "core.agent";
    public const string Condition = "core.condition";
    public const string Merge = "core.merge";

    public static bool IsSupported(string? type) =>
        type is Input or Uppercase or Reverse or Output or McpTool or Agent or Condition or Merge;
}
