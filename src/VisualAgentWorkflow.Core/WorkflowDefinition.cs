using System.Collections.Immutable;
using System.Globalization;
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
    /// <summary>Run-scoped mutable scalar declarations introduced by schema 1.44.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, WorkflowVariableDefinition>? Variables { get; init; }
}

/// <summary>A workflow-owned, non-secret typed scalar represented portably as text.</summary>
public sealed record WorkflowParameterDefinition(string DisplayName, string DefaultValue)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<string>? Choices { get; init; }
}

/// <summary>A workflow-local, run-scoped, non-secret scalar variable declaration.</summary>
public sealed record WorkflowVariableDefinition(string DisplayName, string DefaultValue)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
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
    public NumberConstantConfiguration? NumberConstant { get; init; }
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
    /// <summary>Ordered, bounded execution outputs for a Blueprint-style sequence node.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SequenceConfiguration? Sequence { get; init; }
    /// <summary>Optional static JSON arguments for a direct MCP tool node; credentials and grants are never stored here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? McpArgumentsJson { get; init; }
    /// <summary>Schema-driven top-level argument pins for direct MCP graph schema 1.37.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<McpInputPortDefinition>? McpInputPorts { get; init; }
    /// <summary>Stable variable declaration ID used by VariableGet and VariableSet nodes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableId { get; init; }
}

/// <summary>Portable, normalized metadata for one direct MCP argument pin.</summary>
public sealed record McpInputPortDefinition(string Id, string Name, string DataType, bool Required);

/// <summary>Copy-on-insert source identity; informational provenance, never a grant or runtime resolver.</summary>
public sealed record NodeOrigin(string WorkflowId, Guid RevisionId, int Version, string ContentHash, string NodeId);
public sealed record PortReference(string NodeId, string PortId);
public sealed record ConnectionDefinition(PortReference Source, PortReference Target, string Kind);

/// <summary>The execution subset is a typed graph with explicit routing, bounded parallel blocks, and one validated bounded loop region.</summary>
public static class NodeTypes
{
    public const string Input = "core.input";
    public const string Uppercase = "core.text.uppercase";
    public const string SequenceUppercase = "core.sequence.uppercase";
    public const string SequenceReverse = "core.sequence.reverse";
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
    public const string Sequence = "core.sequence";
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
    public const string StringToInteger = "core.convert.string-to-integer";
    public const string NumberConstant = "core.constant.number";
    public const string NumberAdd = "core.number.add";
    public const string FunctionInput = "core.function.input";
    public const string FunctionOutput = "core.function.output";
    public const string FunctionCall = "core.function.call";
    public const string VariableGet = "core.variable.get";
    public const string VariableSet = "core.variable.set";

    public static bool IsSupported(string? type) => WorkflowNodeContractCatalog.TryGet(type, out _);
}

public sealed record WorkflowSchemaDescriptor(string Version, bool Supported, bool UsesExplicitGraph, bool UsesDirectMcpPins);

/// <summary>Describes one reviewed, explicitly named schema-promotion operation.</summary>
/// <remarks>This is discoverability metadata only. It is not executable migration or a composable version graph.</remarks>
public sealed record WorkflowSchemaPromotionDescriptor(
    string OperationId,
    string FromSchema,
    string ToSchema,
    string GraphEditingMethod,
    bool ChangesTopology);

/// <summary>Exact promotion operations currently documented by the Core editing API.</summary>
public static class WorkflowSchemaPromotionCatalog
{
    public static ImmutableArray<WorkflowSchemaPromotionDescriptor> All { get; } =
    [
        new("explicit-branch", "1.23", "1.24", nameof(GraphEditing.EnableExplicitBranchGraph), false),
        new("typed-boolean", "1.24", "1.25", nameof(GraphEditing.EnableTypedBooleanGraph), false),
        new("boolean-topology", "1.25", "1.26", nameof(GraphEditing.EnableBooleanTopologyGraph), false),
        new("pure-graph-functions", "1.26", "1.27", nameof(GraphEditing.EnablePureGraphFunctions), false),
        new("agent-graph-function-tools", "1.27", "1.28", nameof(GraphEditing.EnableAgentGraphFunctionTools), false),
        new("agent-integer-values", "1.30", "1.31", nameof(GraphEditing.EnableIntegerGraph), false),
        new("string-integer-conversion", "1.31", "1.39", nameof(GraphEditing.EnableStringIntegerConversionGraph), false),
        new("direct-mcp-typed-pins", "1.23", "1.37", nameof(GraphEditing.EnableDirectMcpGraph), false),
        new("finite-number-pins", "1.37", "1.40", nameof(GraphEditing.EnableNumberGraph), false),
        new("number-parameter-propagation", "1.40", "1.41", nameof(GraphEditing.EnableNumberPropagationGraph), false),
        new("number-addition", "1.41", "1.42", nameof(GraphEditing.EnableNumberArithmeticGraph), false),
        new("sequence-paths", "1.38", "1.43", nameof(GraphEditing.EnableSequencePathGraph), false),
        new("sequence-agent-paths", "1.43", "1.46", nameof(GraphEditing.EnableSequenceAgentPathGraph), false),
        new("sequence-transform-paths", "1.43", "1.47", nameof(GraphEditing.EnableSequenceTransformPathGraph), false),
        new("sequence-reverse-paths", "1.47", "1.48", nameof(GraphEditing.EnableSequenceReversePathGraph), false),
        new("sequence-transform-chains-from-uppercase", "1.47", "1.49", nameof(GraphEditing.EnableSequenceTransformChainGraph), false),
        new("sequence-transform-chains-from-reverse", "1.48", "1.49", nameof(GraphEditing.EnableSequenceTransformChainGraph), false),
        new("sequence-workflow-call-paths", "1.49", "1.50", nameof(GraphEditing.EnableSequenceWorkflowCallGraph), true),
        new("sequence-workflow-call-transforms", "1.50", "1.51", nameof(GraphEditing.EnableSequenceWorkflowCallTransformGraph), false),
        new("sequence-workflow-call-agents", "1.51", "1.52", nameof(GraphEditing.EnableSequenceWorkflowCallAgentGraph), false),
        new("sequence-workflow-call-json", "1.52", "1.53", nameof(GraphEditing.EnableSequenceWorkflowCallJsonGraph), false),
        new("sequence-workflow-call-json-chains", "1.53", "1.54", nameof(GraphEditing.EnableSequenceWorkflowCallJsonChainGraph), false),
        new("mutable-variables", "1.23", "1.44", nameof(GraphEditing.EnableMutableVariablesGraph), true)
    ];
}

/// <summary>Exact compatibility metadata for serialized workflow schema versions.</summary>
/// <remarks>This is a support/capability catalog, not a migration registry.</remarks>
public static class WorkflowSchemaCatalog
{
    public static ImmutableArray<WorkflowSchemaDescriptor> All { get; } =
        [.. Enumerable.Range(0, 55).Select(number =>
        {
            var version = $"1.{number}";
            var explicitGraph = version is "1.23" or "1.24" or "1.25" or "1.26" or "1.27" or "1.28" or
                "1.30" or "1.31" or "1.32" or "1.33" or "1.34" or "1.35" or "1.36" or "1.37" or
                "1.38" or "1.39" or "1.40" or "1.41" or "1.42" or "1.43" or "1.44" or "1.45" or "1.46" or "1.47" or "1.48" or "1.49" or "1.50" or "1.51" or "1.52" or "1.53" or "1.54";
            var directMcpPins = version is "1.37" or "1.40" or "1.41" or "1.42" or "1.43" or "1.46" or "1.47" or "1.48" or "1.49" or "1.50" or "1.51" or "1.52" or "1.53" or "1.54";
            return new WorkflowSchemaDescriptor(version, true, explicitGraph, directMcpPins);
        })];

    public static bool IsSupported(string? version) => TryGet(version, out var descriptor) && descriptor.Supported;

    public static bool TryGet(string? version, out WorkflowSchemaDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(candidate => string.Equals(candidate.Version, version, StringComparison.Ordinal))!;
        return descriptor is not null;
    }
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
    public const string DirectMcpPinsGraph = "1.37";
    public const string SequenceGraph = "1.38";
    public const string StringIntegerConversionGraph = "1.39";
    public const string NumberGraph = "1.40";
    public const string NumberPropagationGraph = "1.41";
    public const string NumberArithmeticGraph = "1.42";
    /// <summary>Bounded, disjoint linear downstream paths from ordered Sequence outputs.</summary>
    public const string SequencePathGraph = "1.43";
    /// <summary>Bounded linear run-scoped scalar variable proof graph.</summary>
    public const string MutableVariablesGraph = "1.44";
    /// <summary>Bounded explicit loop with run-scoped scalar variables.</summary>
    public const string LoopVariablesGraph = "1.45";
    /// <summary>Bounded Sequence paths with either direct MCP nodes or one terminal plain-text Agent.</summary>
    public const string SequenceAgentPathGraph = "1.46";
    /// <summary>Bounded Sequence paths with direct MCP nodes followed by one deterministic text transform.</summary>
    public const string SequenceTransformPathGraph = "1.47";
    /// <summary>Bounded Sequence paths with direct MCP nodes followed by one deterministic text transform.</summary>
    public const string SequenceReversePathGraph = "1.48";
    /// <summary>Bounded Sequence paths with direct MCP nodes followed by at most one uppercase and one reverse transform.</summary>
    public const string SequenceTransformChainGraph = "1.49";
    /// <summary>Bounded Sequence paths with direct MCP nodes followed by one terminal pinned string WorkflowCall.</summary>
    public const string SequenceWorkflowCallGraph = "1.50";
    /// <summary>Bounded Sequence paths with a pinned string WorkflowCall followed by one deterministic text transform.</summary>
    public const string SequenceWorkflowCallTransformGraph = "1.51";
    /// <summary>Bounded Sequence paths with a pinned string WorkflowCall followed by one terminal plain-text Agent.</summary>
    public const string SequenceWorkflowCallAgentGraph = "1.52";
    /// <summary>Bounded Sequence paths with a pinned JSON-output WorkflowCall and one pure JSON composition node.</summary>
    public const string SequenceWorkflowCallJsonGraph = "1.53";
    /// <summary>Bounded Sequence paths with a pinned JSON WorkflowCall followed by up to two pure JSON composition nodes.</summary>
    public const string SequenceWorkflowCallJsonChainGraph = "1.54";

    public static bool IsSupported(string? version) => WorkflowSchemaCatalog.IsSupported(version);

    public static bool UsesExplicitGraph(string? version) =>
        WorkflowSchemaCatalog.TryGet(version, out var descriptor) && descriptor.UsesExplicitGraph;

    public static bool UsesDirectMcpPins(string? version) =>
        WorkflowSchemaCatalog.TryGet(version, out var descriptor) && descriptor.UsesDirectMcpPins;

    public static bool UsesExplicitGraph(WorkflowDefinition definition) =>
        UsesExplicitGraph(definition.SchemaVersion) ||
        definition.SchemaVersion is AgentParameterBindings or AgentExecutionRoutes or IntegerGraph or ConfigurableParallelGraph or CollaborativeParallelGraph or BranchLocalParallelGraph or PhasedCollaborativeParallelGraph or DirectMcpGraph or DirectMcpPinsGraph or SequenceGraph or SequencePathGraph or SequenceAgentPathGraph or SequenceTransformPathGraph or SequenceReversePathGraph or SequenceTransformChainGraph or SequenceWorkflowCallGraph or SequenceWorkflowCallTransformGraph or SequenceWorkflowCallAgentGraph or SequenceWorkflowCallJsonGraph or SequenceWorkflowCallJsonChainGraph or StringIntegerConversionGraph or NumberGraph or NumberPropagationGraph or NumberArithmeticGraph or MutableVariablesGraph or LoopVariablesGraph && definition.ExecutionConnections is not null;
}

/// <summary>Runtime value held by one workflow invocation's variable store.</summary>
public sealed record WorkflowVariableValue(string DataType, object Value, long Version);

/// <summary>Creates an isolated, lock-protected store for one workflow invocation.</summary>
public sealed partial class WorkflowVariableStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, WorkflowVariableValue> values = new(StringComparer.Ordinal);
    private long nextVersion;

    public WorkflowVariableStore(WorkflowDefinition definition)
    {
        foreach (var (id, variable) in definition.Variables ?? ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty)
        {
            if (variable is null) continue;
            values[id] = new(variable.Type ?? WorkflowDataTypes.String,
                Parse(variable.Type ?? WorkflowDataTypes.String, variable.DefaultValue), ++nextVersion);
        }
    }

    public bool TryRead(string variableId, out WorkflowVariableValue value)
    {
        lock (sync) return values.TryGetValue(variableId, out value!);
    }

    public WorkflowVariableValue Write(string variableId, string dataType, object value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!values.TryGetValue(variableId, out var previous))
                throw new InvalidOperationException($"Variable '{variableId}' is not declared.");
            if (!string.Equals(previous.DataType, dataType, StringComparison.Ordinal))
                throw new InvalidOperationException($"Variable '{variableId}' requires {previous.DataType}, not {dataType}.");
            var written = new WorkflowVariableValue(dataType, value, ++nextVersion);
            values[variableId] = written;
            return written;
        }
    }

    public static object Parse(string dataType, string value) => dataType switch
    {
        WorkflowDataTypes.String when value.Length <= 16_384 => value,
        WorkflowDataTypes.Boolean when bool.TryParse(value, out var boolean) => boolean,
        WorkflowDataTypes.Integer when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => integer,
        WorkflowDataTypes.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => number,
        _ => throw new ArgumentException($"The default value is invalid for variable type '{dataType}'.", nameof(value))
    };
}

public sealed record StringConstantConfiguration(string Value);
public sealed record BooleanConstantConfiguration(bool Value);
public sealed record IntegerConstantConfiguration(long Value);
public sealed record NumberConstantConfiguration(double Value);

public sealed record ParallelForkConfiguration(ImmutableArray<ParallelBranchDescriptor> Branches);
public sealed record ParallelBranchDescriptor(string Id, string Label, string AnchorNodeId);
public sealed record ParallelBranchConfiguration(string ForkNodeId, string BranchId);
public sealed record ParallelJoinConfiguration(string ForkNodeId, ImmutableArray<string> BranchIds);
public sealed record ParallelRendezvousConfiguration(ImmutableArray<string> BranchIds);
public sealed record SequenceConfiguration(ImmutableArray<SequenceOutputDescriptor> Outputs);
public sealed record SequenceOutputDescriptor(string Id, string Label);
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

/// <summary>The immutable identity tuple for the exact child release used by a WorkflowCall.</summary>
public sealed record WorkflowCallIdentity(string WorkflowId, Guid RevisionId, int Version, string ContentHash);

/// <summary>An immutable, self-contained reference to the exact child release used by this node.</summary>
public sealed record WorkflowCallConfiguration(string WorkflowId, Guid RevisionId, int Version, string ContentHash,
    WorkflowDefinition Definition)
{
    /// <summary>Returns the identity tuple without exposing the embedded snapshot as identity.</summary>
    [JsonIgnore]
    public WorkflowCallIdentity Identity => new(WorkflowId, RevisionId, Version, ContentHash);

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
