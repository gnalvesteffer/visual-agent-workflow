using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>Portable definition. UI layout and environment bindings are stored separately.</summary>
public sealed record WorkflowDefinition(
    string SchemaVersion,
    string WorkflowId,
    string EntryNodeId,
    ImmutableSortedDictionary<string, NodeDefinition> Nodes,
    ImmutableSortedDictionary<string, ConnectionDefinition> Connections)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, WorkflowParameterDefinition>? Parameters { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, WorkflowSkill>? Skills { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, AgentProfile>? AgentProfiles { get; init; }
}

/// <summary>A workflow-owned, non-secret typed scalar represented portably as text.</summary>
public sealed record WorkflowParameterDefinition(string DisplayName, string DefaultValue)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<string>? Choices { get; init; }
}

public sealed record NodeDefinition(string Type, string TypeVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolBindingRequest? Tool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentConfiguration? Agent = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeOrigin? Origin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringCondition? Condition { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApprovalConfiguration? Approval { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParameterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputDataType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputDataType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonPropertySelection? JsonPropertySelection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonProjectionConfiguration? JsonProjection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptTemplate? TextTemplate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LoopConfiguration? Loop { get; init; }
}

/// <summary>Copy-on-insert source identity; informational provenance, never a grant or runtime resolver.</summary>
public sealed record NodeOrigin(string WorkflowId, Guid RevisionId, int Version, string ContentHash, string NodeId);
public sealed record PortReference(string NodeId, string PortId);
public sealed record ConnectionDefinition(PortReference Source, PortReference Target, string Kind);

/// <summary>The execution subset is a typed graph with explicit routing, bounded parallel blocks, and one validated bounded loop region.</summary>
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
    public const string Approval = "core.approval";
    public const string WorkflowValue = "core.workflow-value";
    public const string JsonSelectString = "core.json.select-string";
    public const string JsonProject = "core.json.project";
    public const string TextTemplate = "core.text.template";
    public const string ParallelFork = "core.parallel.fork";
    public const string ParallelBranch = "core.parallel.branch";
    public const string ParallelJoin = "core.parallel.join";
    public const string Loop = "core.loop";
    public const string LoopBody = "core.loop.body";
    public const string Terminate = "core.terminate";

    public static bool IsSupported(string? type) =>
        type is Input or Uppercase or Reverse or Output or McpTool or Agent or Condition or Merge or Approval or WorkflowValue or JsonSelectString or JsonProject or TextTemplate or ParallelFork or ParallelBranch or ParallelJoin or Loop or LoopBody or Terminate;
}

public sealed record LoopConfiguration(StringCondition ContinueWhile, int MaximumIterations);

/// <summary>Selects a JSON value through a bounded RFC 6901 JSON Pointer.</summary>
public sealed record JsonPropertySelection(string Pointer);
public sealed record JsonProjectionConfiguration(ImmutableArray<JsonProjectionField> Fields);
public sealed record JsonProjectionField(string Name, string Pointer)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ValueType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonValueSchema? ValueSchema { get; init; }
}
public sealed record JsonValueSchema(string Type)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<JsonValueProperty>? Properties { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonValueSchema? Items { get; init; }
}
public sealed record JsonValueProperty(string Name, string Pointer, JsonValueSchema Schema)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Optional { get; init; }
}
