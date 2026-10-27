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
    /// <summary>Optional user-facing workflow name. WorkflowId remains the stable identity used for storage and references.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, WorkflowParameterDefinition>? Parameters { get; init; }
    /// <summary>Explicit execution activations used by schemas 1.23+; legacy versions schedule through Connections.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, ConnectionDefinition>? ExecutionConnections { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, WorkflowSkill>? Skills { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, AgentProfile>? AgentProfiles { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, ParallelCollaborationRegion>? ParallelCollaborationRegions { get; init; }
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
    public TerminationConfiguration? Termination { get; init; }
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
    public StringConstantConfiguration? StringConstant { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BooleanConstantConfiguration? BooleanConstant { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IntegerConstantConfiguration? IntegerConstant { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptTemplate? TextTemplate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LoopConfiguration? Loop { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowCallConfiguration? WorkflowCall { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphFunctionConfiguration? GraphFunction { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionPortId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParallelForkConfiguration? ParallelFork { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParallelBranchConfiguration? ParallelBranch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParallelJoinConfiguration? ParallelJoin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParallelRendezvousConfiguration? ParallelRendezvous { get; init; }
    /// <summary>Optional static JSON arguments for a direct MCP tool node; credentials and grants are never stored here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? McpArgumentsJson { get; init; }
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
    public const string ParallelRendezvous = "core.parallel.rendezvous";
    public const string Loop = "core.loop";
    public const string LoopBody = "core.loop.body";
    public const string Terminate = "core.terminate";
    public const string WorkflowCall = "core.workflow-call";
    public const string StringConstant = "core.constant.string";
    public const string TextConcat = "core.text.concat";
    public const string StringEquals = "core.text.equals";
    public const string BooleanBranch = "core.branch.boolean";
    public const string BooleanConstant = "core.constant.boolean";
    public const string BooleanNot = "core.boolean.not";
    public const string IntegerConstant = "core.constant.integer";
    public const string IntegerAdd = "core.integer.add";
    public const string IntegerEquals = "core.integer.equals";
    public const string FunctionInput = "core.function.input";
    public const string FunctionOutput = "core.function.output";
    public const string FunctionCall = "core.function.call";

    public static bool IsSupported(string? type) =>
        type is Input or Uppercase or Reverse or Output or McpTool or Agent or Condition or Merge or Approval or WorkflowValue or JsonSelectString or JsonProject or TextTemplate or ParallelFork or ParallelBranch or ParallelJoin or ParallelRendezvous or Loop or LoopBody or Terminate or WorkflowCall or StringConstant or TextConcat or StringEquals or BooleanBranch or BooleanConstant or BooleanNot or IntegerConstant or IntegerAdd or IntegerEquals or FunctionInput or FunctionOutput or FunctionCall;
}

public static class WorkflowSchemaVersions
{
    public const string ExplicitGraph = "1.23";
    public const string ExplicitBranchGraph = "1.24";
    public const string TypedBooleanGraph = "1.25";
    public const string BooleanTopologyGraph = "1.26";
    public const string PureGraphFunctions = "1.27";
    public const string AgentGraphFunctionTools = "1.28";
    public const string AgentParameterBindings = "1.29";
    public const string AgentExecutionRoutes = "1.30";
    public const string IntegerGraph = "1.31";
    public const string ConfigurableParallelGraph = "1.32";
    public const string CollaborativeParallelGraph = "1.33";
    public const string BranchLocalParallelGraph = "1.34";
    public const string PhasedCollaborativeParallelGraph = "1.35";
    public const string DirectMcpGraph = "1.36";

    public static bool UsesExplicitGraph(string? version) => version is ExplicitGraph or ExplicitBranchGraph or TypedBooleanGraph or BooleanTopologyGraph or PureGraphFunctions or AgentGraphFunctionTools or AgentExecutionRoutes or IntegerGraph or ConfigurableParallelGraph or CollaborativeParallelGraph or BranchLocalParallelGraph or PhasedCollaborativeParallelGraph or DirectMcpGraph;

    public static bool UsesExplicitGraph(WorkflowDefinition definition) =>
        UsesExplicitGraph(definition.SchemaVersion) ||
        definition.SchemaVersion is AgentParameterBindings or AgentExecutionRoutes or IntegerGraph or ConfigurableParallelGraph or CollaborativeParallelGraph or BranchLocalParallelGraph or PhasedCollaborativeParallelGraph or DirectMcpGraph && definition.ExecutionConnections is not null;
}

public sealed record StringConstantConfiguration(string Value);
public sealed record BooleanConstantConfiguration(bool Value);
public sealed record IntegerConstantConfiguration(long Value);

public sealed record ParallelForkConfiguration(ImmutableArray<ParallelBranchDescriptor> Branches);
public sealed record ParallelBranchDescriptor(string Id, string Label, string AnchorNodeId);
public sealed record ParallelBranchConfiguration(string ForkNodeId, string BranchId);
public sealed record ParallelJoinConfiguration(string ForkNodeId, ImmutableArray<string> BranchIds);
public sealed record ParallelRendezvousConfiguration(ImmutableArray<string> BranchIds);
public sealed record ParallelCollaborationRegion(string ForkNodeId, string RendezvousNodeId, string JoinNodeId,
    ImmutableArray<string> ChannelIds, int? TimeoutSeconds, ImmutableArray<ParallelCollaborationBranch> Branches)
{
    /// <summary>The second ordered barrier, required by schema 1.35 and omitted by schema 1.33.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondRendezvousNodeId { get; init; }
}
public sealed record ParallelCollaborationBranch(string BranchId, ImmutableArray<ParallelCollaborationAgentGrant> Agents);
public sealed record ParallelCollaborationAgentGrant(string AgentNodeId, string Phase, ImmutableArray<string> Channels)
{
    /// <summary>The operation granted in schema 1.35; omitted from schema-1.33 grants.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }
}

public static class ParallelCollaborationPhases
{
    public const string Publish = "publish";
    public const string Read = "read";
    public const string Phase1 = "phase1";
    public const string Phase2 = "phase2";
}

public static class ParallelCollaborationRoles
{
    public const string Publish = "publish";
    public const string Read = "read";
}

public static class ParallelCollaborationLimits
{
    public const int MaximumChannels = 8;
    public const int MaximumChannelIdLength = 64;
    public const int MaximumMessageCharacters = 4_000;
    public const int MaximumMessagesPerChannel = 32;
    public const int MaximumMessagesPerRegion = 64;
    public const int MaximumSnapshotBytes = 65_536;

    public static bool IsValidChannelId(string? id) => id is { Length: > 0 and <= MaximumChannelIdLength } &&
        id[0] is >= 'a' and <= 'z' && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

public sealed record LoopConfiguration(StringCondition ContinueWhile, int MaximumIterations);
public sealed record TerminationConfiguration(string Outcome);

public static class WorkflowTerminationOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
}

/// <summary>An immutable, self-contained reference to the exact child release used by this node.</summary>
public sealed record WorkflowCallConfiguration(string WorkflowId, Guid RevisionId, int Version, string ContentHash,
    WorkflowDefinition Definition)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<WorkflowCallParameterMapping>? ParameterMappings { get; init; }
}

/// <summary>Maps one parent-owned workflow parameter to a child parameter; values are resolved per run.</summary>
public sealed record WorkflowCallParameterMapping(string ChildParameterId, string ParentParameterId);

public static class WorkflowCallLimits
{
    public const int MaximumDepth = 4;
    public const int MaximumExpandedNodes = 1024;
}

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
