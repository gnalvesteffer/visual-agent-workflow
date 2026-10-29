using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Versioned declarative pin metadata for a static node-family slice.</summary>
public sealed record WorkflowNodeDescriptor(
    string NodeType,
    string DescriptorVersion,
    string ConfigurationKind,
    ImmutableArray<WorkflowPortDescriptor> Ports)
{
    /// <summary>Minimum explicit graph schema that may use this static descriptor.</summary>
    public string? MinimumSchemaVersion { get; init; }

    public WorkflowNodeContract NodeContract => WorkflowNodeContractCatalog.Get(NodeType);

    /// <summary>Lifecycle and authority metadata projected from the canonical node contract.</summary>
    public WorkflowNodeActivationKind Activation => NodeContract.Activation;
    public WorkflowNodeStateKind State => NodeContract.State;
    public WorkflowNodeFailureKind Failure => NodeContract.Failure;
    public WorkflowNodeEvidenceKind Evidence => NodeContract.Evidence;
    public WorkflowNodeEffectFlags Effects => NodeContract.Effects;
    public WorkflowNodeAuthoringFlags Authoring => NodeContract.Authoring;
    public bool RequiresHostCapability => NodeContract.RequiresHostCapability;
    public bool SupportsCancellation => NodeContract.SupportsCancellation;
    public ImmutableArray<WorkflowNodeLimit> Limits => NodeContract.Limits;
}

public enum WorkflowNodeDescriptorResolutionKind
{
    Resolved,
    UnsupportedNodeType,
    UnsupportedNodeVersion,
    UnsupportedSchema,
    MalformedConfiguration,
    DiscoveryUnavailable
}

/// <summary>A fail-closed descriptor resolution result with an explicit authority state.</summary>
public sealed record WorkflowNodeDescriptorResolution(
    WorkflowNodeDescriptorResolutionKind Kind,
    WorkflowNodeDescriptor? Descriptor,
    string Message)
{
    public bool IsResolved => Kind == WorkflowNodeDescriptorResolutionKind.Resolved && Descriptor is not null;
}

/// <summary>Pairs one resolved MCP argument pin with its persisted tool-property identity.</summary>
public sealed record WorkflowMcpArgumentDescriptor(
    WorkflowPortDescriptor Port,
    McpInputPortDefinition Argument);

public sealed record WorkflowNodeDescriptorIssue(string Code, string Message);

/// <summary>A diagnostic emitted by the advisory cross-layer descriptor audit.</summary>
/// <remarks>
/// These diagnostics identify metadata drift for maintainers and tests only. They are
/// intentionally not part of workflow validation, persistence, admission, or runtime
/// authority.
/// </remarks>
public sealed record WorkflowNodeDescriptorAuditIssue(
    string Code,
    string NodeType,
    string Authority,
    string Message);

/// <summary>A deterministic, read-only audit finding for one persisted workflow node.</summary>
public sealed record WorkflowDefinitionDescriptorAuditIssue(
    string Code,
    string Path,
    string Message);

/// <summary>
/// Audits descriptor resolution for persisted definitions without consulting live discovery.
/// This is advisory evidence for convergence work; it does not replace schema validation,
/// runtime dispatch, host admission, or historical playback authorities.
/// </summary>
public static class WorkflowDefinitionDescriptorAudit
{
    public static ImmutableArray<WorkflowDefinitionDescriptorAuditIssue> Audit(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<WorkflowDefinitionDescriptorAuditIssue>();
        var ancestry = new HashSet<string>(StringComparer.Ordinal) { definition.WorkflowId };
        AuditDefinition(definition, "", ancestry, issues);
        return issues.ToImmutable();
    }

    private static void AuditDefinition(WorkflowDefinition definition, string path,
        HashSet<string> ancestry, ImmutableArray<WorkflowDefinitionDescriptorAuditIssue>.Builder issues)
    {
        if (definition.Nodes is null) return;
        foreach (var (nodeId, node) in definition.Nodes)
        {
            var nodePath = string.IsNullOrEmpty(path) ? $"nodes.{nodeId}" : $"{path}nodes.{nodeId}";
            if (node is null)
            {
                issues.Add(new("node.null", nodePath, "The persisted node is null."));
                continue;
            }

            var resolution = WorkflowNodeDescriptorResolver.ResolveForDefinition(definition, node);
            if (!resolution.IsResolved)
            {
                var issueCode = resolution.Kind switch
                {
                    WorkflowNodeDescriptorResolutionKind.UnsupportedNodeType => "node.type",
                    WorkflowNodeDescriptorResolutionKind.UnsupportedNodeVersion => "node.version",
                    WorkflowNodeDescriptorResolutionKind.UnsupportedSchema => "node.schema",
                    WorkflowNodeDescriptorResolutionKind.MalformedConfiguration when node.Type == NodeTypes.McpTool => "mcp.mapping",
                    _ => "descriptor.unresolved"
                };
                issues.Add(new(issueCode, nodePath + ".descriptor", resolution.Message));
                continue;
            }
            var descriptor = resolution.Descriptor!;

            AddProjectionIssues(node, descriptor, nodePath, issues);

            if (node.WorkflowCall?.Definition is { } child && ancestry.Add(child.WorkflowId))
            {
                AuditDefinition(child, nodePath + ".workflowCall.definition.", ancestry, issues);
                ancestry.Remove(child.WorkflowId);
            }
        }
    }

    private static void AddProjectionIssues(NodeDefinition node, WorkflowNodeDescriptor descriptor, string nodePath,
        ImmutableArray<WorkflowDefinitionDescriptorAuditIssue>.Builder issues)
    {
        void Compare<T>(string code, IEnumerable<T> expected, IEnumerable<T> actual, string message)
        {
            if (!expected.SequenceEqual(actual))
                issues.Add(new(code, nodePath + ".descriptor", message));
        }

        Compare("ports.data-inputs", WorkflowPorts.ExplicitDataInputs(node).Select(port => (port.Id, port.DataType)),
            descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)
                .Select(port => (port.Id, port.DataType!)),
            "Descriptor data inputs differ from the persisted Core port projection.");
        Compare("ports.data-outputs", WorkflowPorts.ExplicitDataOutputs(node).Select(port => (port.Id, port.DataType)),
            descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data)
                .Select(port => (port.Id, port.DataType!)),
            "Descriptor data outputs differ from the persisted Core port projection.");
        Compare("ports.execution-inputs", WorkflowPorts.ExplicitExecutionInputs(node),
            descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Execution)
                .Select(port => port.Id),
            "Descriptor execution inputs differ from the persisted Core port projection.");
        Compare("ports.execution-outputs", WorkflowPorts.ExplicitExecutionOutputs(node),
            descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Execution)
                .Select(port => port.Id),
            "Descriptor execution outputs differ from the persisted Core port projection.");
    }
}

/// <summary>
/// Compares the non-authoritative node descriptor projection with the existing Core
/// consumers that expose the same configured pin shape.
/// </summary>
public static class WorkflowNodeDescriptorAudit
{
    public static ImmutableArray<WorkflowNodeDescriptorAuditIssue> AuditCatalog()
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorAuditIssue>();
        foreach (var descriptor in WorkflowNodeDescriptorRegistry.All)
            Audit(new NodeDefinition(descriptor.NodeType, descriptor.NodeContract.TypeVersion), issues, descriptor);

        return issues.ToImmutable();
    }

    /// <summary>Audits one safe default configuration for every canonical node family.</summary>
    public static ImmutableArray<WorkflowNodeDescriptorAuditIssue> AuditNodeFamilies()
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorAuditIssue>();
        foreach (var contract in WorkflowNodeContractCatalog.All)
        {
            var node = new NodeDefinition(contract.Type, contract.TypeVersion);
            if (!WorkflowNodeDescriptorResolver.TryResolve(node, null, null, out var descriptor))
            {
                issues.Add(new("descriptor.unresolved", node.Type, "descriptor",
                    "The default node configuration cannot be represented by the current descriptor projection."));
                continue;
            }

            Audit(node, issues, descriptor);
        }

        return issues.ToImmutable();
    }

    public static ImmutableArray<WorkflowNodeDescriptorAuditIssue> Audit(
        NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorAuditIssue>();
        if (!WorkflowNodeDescriptorResolver.TryResolve(node, discoveredMcpInputPorts, discoveredMcpInputDescriptions, out var descriptor))
        {
            issues.Add(new("descriptor.unresolved", node.Type, "descriptor",
                "The configured node cannot be represented by the current descriptor projection."));
            return issues.ToImmutable();
        }

        Audit(node, issues, descriptor);
        return issues.ToImmutable();
    }

    /// <summary>
    /// Audits an explicitly supplied descriptor. This overload lets tests detect
    /// drift without mutating the canonical registry or workflow definitions.
    /// </summary>
    public static ImmutableArray<WorkflowNodeDescriptorAuditIssue> Audit(
        NodeDefinition node,
        WorkflowNodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(descriptor);
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorAuditIssue>();
        Audit(node, issues, descriptor);
        return issues.ToImmutable();
    }

    private static void Audit(
        NodeDefinition node,
        ImmutableArray<WorkflowNodeDescriptorAuditIssue>.Builder issues,
        WorkflowNodeDescriptor descriptor)
    {
        void Issue(string code, string authority, string message) =>
            issues.Add(new(code, node.Type, authority, message));

        foreach (var conformanceIssue in WorkflowNodeDescriptorConformance.ValidateResolved(node))
            Issue($"descriptor.{conformanceIssue.Code}", "descriptor", conformanceIssue.Message);

        var expectedDataInputs = WorkflowPorts.ExplicitDataInputs(node)
            .Select(port => (port.Id, port.DataType)).ToArray();
        var actualDataInputs = descriptor.Ports
            .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)
            .Select(port => (port.Id, port.DataType!)).ToArray();
        if (!expectedDataInputs.SequenceEqual(actualDataInputs))
            Issue("ports.data-inputs", "port-projection", "Descriptor data inputs differ from the canonical Core port projection.");

        var expectedDataOutputs = WorkflowPorts.ExplicitDataOutputs(node)
            .Select(port => (port.Id, port.DataType)).ToArray();
        var actualDataOutputs = descriptor.Ports
            .Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data)
            .Select(port => (port.Id, port.DataType!)).ToArray();
        if (!expectedDataOutputs.SequenceEqual(actualDataOutputs))
            Issue("ports.data-outputs", "port-projection", "Descriptor data outputs differ from the canonical Core port projection.");

        var expectedExecutionInputs = WorkflowPorts.ExplicitExecutionInputs(node);
        var actualExecutionInputs = descriptor.Ports
            .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Execution)
            .Select(port => port.Id).ToImmutableArray();
        if (!expectedExecutionInputs.SequenceEqual(actualExecutionInputs))
            Issue("ports.execution-inputs", "port-projection", "Descriptor execution inputs differ from the canonical Core port projection.");

        var expectedExecutionOutputs = WorkflowPorts.ExplicitExecutionOutputs(node);
        var actualExecutionOutputs = descriptor.Ports
            .Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Execution)
            .Select(port => port.Id).ToImmutableArray();
        if (!expectedExecutionOutputs.SequenceEqual(actualExecutionOutputs))
            Issue("ports.execution-outputs", "port-projection", "Descriptor execution outputs differ from the canonical Core port projection.");

        if (!WorkflowNodeCapabilityProjectionResolver.TryResolve(node, out var projection) ||
            !DescriptorEquivalent(projection.Descriptor, descriptor))
            Issue("projection.descriptor", "capability-projection", "Capability projection does not expose the audited descriptor.");
    }

    private static bool DescriptorEquivalent(WorkflowNodeDescriptor left, WorkflowNodeDescriptor right) =>
        string.Equals(left.NodeType, right.NodeType, StringComparison.Ordinal) &&
        string.Equals(left.DescriptorVersion, right.DescriptorVersion, StringComparison.Ordinal) &&
        string.Equals(left.ConfigurationKind, right.ConfigurationKind, StringComparison.Ordinal) &&
        left.Ports.SequenceEqual(right.Ports);
}

/// <summary>Checks the schema-neutral invariants of a registered static descriptor.</summary>
/// <remarks>
/// This is deliberately limited to descriptors whose pin shape is independent of node configuration.
/// Dynamic node configuration remains validated by its schema-specific boundary.
/// </remarks>
public static class WorkflowNodeDescriptorConformance
{
    public static ImmutableArray<WorkflowNodeDescriptorIssue> Validate(WorkflowNodeDescriptor descriptor)
        => ValidateCore(descriptor, allowDynamic: false);

    /// <summary>
    /// Validates the complete bounded static registry. The registry is an explicit
    /// non-serialized pin-descriptor slice; configuration-derived node families are
    /// intentionally validated through <see cref="ValidateResolved"/> instead.
    /// </summary>
    public static ImmutableArray<WorkflowNodeDescriptorIssue> ValidateRegistry()
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorIssue>();
        void Error(string code, string message) => issues.Add(new(code, message));

        if (WorkflowNodeDescriptorRegistry.All.IsDefaultOrEmpty)
        {
            Error("registry.empty", "The bounded static descriptor registry must not be empty.");
            return issues.ToImmutable();
        }

        foreach (var duplicate in WorkflowNodeDescriptorRegistry.All
                     .GroupBy(descriptor => (descriptor.NodeType, descriptor.DescriptorVersion))
                     .Where(group => group.Count() > 1))
            Error("registry.duplicate", $"The descriptor key '{duplicate.Key.NodeType}@{duplicate.Key.DescriptorVersion}' must be unique.");

        foreach (var duplicate in WorkflowNodeDescriptorRegistry.All
                     .GroupBy(descriptor => descriptor.NodeType, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            Error("registry.node-type", $"The bounded static registry may contain only one descriptor for '{duplicate.Key}'.");

        foreach (var descriptor in WorkflowNodeDescriptorRegistry.All)
        {
            if (!WorkflowNodeContractCatalog.TryGet(descriptor.NodeType, out var contract))
            {
                Error("registry.contract", $"Descriptor '{descriptor.NodeType}' must map to a known node contract.");
                continue;
            }

            if (contract.HasDynamicPins)
                Error("registry.dynamic", $"Configuration-derived node '{descriptor.NodeType}' cannot be registered as static.");
            if (string.IsNullOrWhiteSpace(contract.TypeVersion))
                Error("registry.contract-version", $"Descriptor '{descriptor.NodeType}' must map to a versioned node contract.");

            foreach (var issue in Validate(descriptor))
                issues.Add(new($"registry.{issue.Code}", issue.Message));
        }

        return issues.ToImmutable();
    }

    /// <summary>Validates the schema-neutral shape of a configuration-derived descriptor.</summary>
    /// <remarks>Dynamic contract shape checks remain schema-specific; this only checks the resolved pin metadata.</remarks>
    public static ImmutableArray<WorkflowNodeDescriptorIssue> ValidateResolved(
        NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts = null,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        return WorkflowNodeDescriptorResolver.TryResolve(node, discoveredMcpInputPorts, discoveredMcpInputDescriptions, out var descriptor)
            ? ValidateCore(descriptor, allowDynamic: true)
            : [new("node-type", "The descriptor must name a supported node family.")];
    }

    private static ImmutableArray<WorkflowNodeDescriptorIssue> ValidateCore(WorkflowNodeDescriptor descriptor, bool allowDynamic)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeDescriptorIssue>();
        void Error(string code, string message) => issues.Add(new(code, message));

        if (string.IsNullOrWhiteSpace(descriptor.NodeType) ||
            !WorkflowNodeContractCatalog.TryGet(descriptor.NodeType, out var contract))
        {
            Error("node-type", "The descriptor must name a supported node family.");
            return issues.ToImmutable();
        }

        if (!string.Equals(descriptor.DescriptorVersion, WorkflowNodeDescriptorRegistry.CurrentVersion, StringComparison.Ordinal))
            Error("version", $"The descriptor version must be {WorkflowNodeDescriptorRegistry.CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(descriptor.ConfigurationKind))
            Error("configuration-kind", "The descriptor must declare a configuration kind.");
        if (!allowDynamic && descriptor.Ports.IsDefaultOrEmpty)
            Error("ports", "A static descriptor must declare at least one port.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var resolvedIds = new HashSet<(WorkflowPortDirection Direction, WorkflowPortKind Kind, string Id)>();
        foreach (var port in descriptor.Ports)
        {
            var unique = allowDynamic
                ? resolvedIds.Add((port.Direction, port.Kind, port.Id))
                : ids.Add(port.Id);
            if (string.IsNullOrWhiteSpace(port.Id) || !unique)
                Error("port-id", $"Port IDs must be nonempty and unique (duplicate '{port.Id}').");
            if (!Enum.IsDefined(port.Direction) || !Enum.IsDefined(port.Kind))
                Error("port-kind", $"Port '{port.Id}' has an unsupported direction or kind.");
            if (port.Kind == WorkflowPortKind.Data && !WorkflowDataTypeCatalog.IsSupported(port.DataType))
                Error("data-type", $"Data port '{port.Id}' must declare a supported data type.");
            if (port.Kind == WorkflowPortKind.Execution && port.DataType is not null)
                Error("execution-type", $"Execution port '{port.Id}' cannot declare a data type.");
            if (!Enum.IsDefined(port.Cardinality))
                Error("cardinality", $"Port '{port.Id}' has an unsupported cardinality.");
            if (port.SupportsStaticValue && (port.Direction != WorkflowPortDirection.Input || port.Kind != WorkflowPortKind.Data))
                Error("static-value", $"Only data input ports may support a static value.");
            if (port.Required && port.Direction != WorkflowPortDirection.Input && port.Kind != WorkflowPortKind.Execution)
                Error("requiredness", $"Only data input ports and execution outputs may be required.");
        }

        var hasExecution = descriptor.Ports.Any(port => port.Kind == WorkflowPortKind.Execution);
        var hasData = descriptor.Ports.Any(port => port.Kind == WorkflowPortKind.Data);
        if (!allowDynamic && contract.HasExecutionPins != hasExecution)
            Error("contract-execution", $"Descriptor execution-pin presence disagrees with the '{contract.Type}' node contract.");
        if (!allowDynamic && contract.HasDataPins != hasData)
            Error("contract-data", $"Descriptor data-pin presence disagrees with the '{contract.Type}' node contract.");
        if (!allowDynamic && contract.IsPure && hasExecution)
            Error("contract-purity", $"Pure node contract '{contract.Type}' cannot declare execution pins.");
        if (!allowDynamic && contract.HasDynamicPins)
            Error("dynamic", $"Dynamic node contract '{contract.Type}' requires a configuration-derived descriptor.");

        return issues.ToImmutable();
    }
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
        Descriptor(NodeTypes.Uppercase, "uppercase", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)], "1.23"),
        Descriptor(NodeTypes.SequenceUppercase, "sequence-uppercase", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.String), ExecutionInput("execute"), ExecutionOutput("then")], "1.47"),
        Descriptor(NodeTypes.Reverse, "reverse", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)], "1.23"),
        Descriptor(NodeTypes.StringConstant, "string-constant", [Output(WorkflowDataTypes.String)], "1.23"),
        Descriptor(NodeTypes.BooleanConstant, "boolean-constant", [Output(WorkflowDataTypes.Boolean)], "1.25"),
        Descriptor(NodeTypes.IntegerConstant, "integer-constant", [Output(WorkflowDataTypes.Integer)], "1.31"),
        Descriptor(NodeTypes.NumberConstant, "number-constant", [Output(WorkflowDataTypes.Number)], "1.40"),
        Descriptor(NodeTypes.NumberAdd, "number-add", [Input("left", WorkflowDataTypes.Number), Input("right", WorkflowDataTypes.Number), Output(WorkflowDataTypes.Number)], "1.42"),
        Descriptor(NodeTypes.StringToInteger, "string-to-integer", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.Integer)], "1.39"),
        Descriptor(NodeTypes.TextConcat, "text-concat", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)], "1.23"),
        Descriptor(NodeTypes.StringEquals, "string-equals", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.Boolean)], "1.24"),
        Descriptor(NodeTypes.IntegerAdd, "integer-add", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Integer)], "1.31"),
        Descriptor(NodeTypes.IntegerEquals, "integer-equals", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Boolean)], "1.31"),
        Descriptor(NodeTypes.BooleanNot, "boolean-not", [Input("value", WorkflowDataTypes.Boolean), Output(WorkflowDataTypes.Boolean)], "1.25"),
        Descriptor(NodeTypes.BooleanBranch, "boolean-branch", [
            Input("condition", WorkflowDataTypes.Boolean),
            ExecutionInput("execute"),
            ExecutionOutput("true"),
            ExecutionOutput("false")], "1.24")
    ];

    public static bool TryGet(string? nodeType, string descriptorVersion, out WorkflowNodeDescriptor descriptor)
    {
        descriptor = All.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeType, nodeType, StringComparison.Ordinal) &&
            string.Equals(candidate.DescriptorVersion, descriptorVersion, StringComparison.Ordinal))!;
        return descriptor is not null;
    }

    /// <summary>Resolves the current non-serialized descriptor only when the node uses its exact implementation version.</summary>
    public static bool TryGetForNode(NodeDefinition node, out WorkflowNodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        return TryGet(node.Type, CurrentVersion, out descriptor) &&
            string.Equals(node.TypeVersion, descriptor.NodeContract.TypeVersion, StringComparison.Ordinal);
    }

    public static WorkflowNodeDescriptor Get(string nodeType, string descriptorVersion = CurrentVersion) =>
        TryGet(nodeType, descriptorVersion, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"No workflow node descriptor '{nodeType}@{descriptorVersion}' is registered.");

    private static WorkflowNodeDescriptor Descriptor(string nodeType, string configurationKind,
        ImmutableArray<WorkflowPortDescriptor> ports, string minimumSchemaVersion) =>
        new(nodeType, CurrentVersion, configurationKind, ports) { MinimumSchemaVersion = minimumSchemaVersion };

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

    private static WorkflowPortDescriptor ExecutionInput(string id) =>
        new(id, WorkflowPortDirection.Input, WorkflowPortKind.Execution)
        {
            Required = true,
            Cardinality = WorkflowPortCardinality.ExactlyOne
        };

    private static WorkflowPortDescriptor ExecutionOutput(string id) =>
        new(id, WorkflowPortDirection.Output, WorkflowPortKind.Execution)
        {
            Required = true,
            Cardinality = WorkflowPortCardinality.ExactlyOne
        };
}

/// <summary>Projects the effective pin contract for a configured node without changing the node definition.</summary>
/// <remarks>
/// This is a shape-only projection. Schema admission, validation, persistence, runtime execution, and host authority
/// remain owned by their existing boundaries. Live MCP metadata is optional authoring input and is never persisted.
/// </remarks>
public static class WorkflowNodeDescriptorResolver
{
    /// <summary>Resolves persisted configuration with the containing workflow's schema boundary applied.</summary>
    public static WorkflowNodeDescriptorResolution ResolveForDefinition(WorkflowDefinition definition, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(node);
        if (!WorkflowSchemaCatalog.IsSupported(definition.SchemaVersion) ||
            !WorkflowSchemaVersions.UsesExplicitGraph(definition))
            return new(WorkflowNodeDescriptorResolutionKind.UnsupportedSchema, null,
                $"Schema '{definition.SchemaVersion}' does not provide the explicit descriptor contract.");

        var resolved = ResolvePersisted(node);
        if (!resolved.IsResolved || !WorkflowNodeDescriptorRegistry.TryGetForNode(node, out var staticDescriptor) ||
            staticDescriptor.MinimumSchemaVersion is not { } minimum || AtLeast(definition.SchemaVersion, minimum))
            return resolved;
        return new(WorkflowNodeDescriptorResolutionKind.UnsupportedSchema, null,
            $"Node type '{node.Type}' requires schema {minimum} or later.");
    }

    /// <summary>
    /// Resolves only persisted node configuration. It never performs discovery and never
    /// converts an unsupported node into an empty, apparently valid pin shape.
    /// </summary>
    public static WorkflowNodeDescriptorResolution ResolvePersisted(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!WorkflowNodeContractCatalog.TryGet(node.Type, out var contract))
            return new(WorkflowNodeDescriptorResolutionKind.UnsupportedNodeType, null,
                $"Node type '{node.Type}' is not registered.");
        if (!string.Equals(node.TypeVersion, contract.TypeVersion, StringComparison.Ordinal))
            return new(WorkflowNodeDescriptorResolutionKind.UnsupportedNodeVersion, null,
                $"Node type '{node.Type}' uses unsupported implementation version '{node.TypeVersion}'.");
        if (!TryResolve(node, null, null, out var descriptor))
            return new(WorkflowNodeDescriptorResolutionKind.MalformedConfiguration, null,
                "The persisted node configuration cannot be represented by the descriptor resolver.");
        if (node.Type == NodeTypes.McpTool && !TryResolveMcpArguments(node, out _))
            return new(WorkflowNodeDescriptorResolutionKind.MalformedConfiguration, null,
                "Persisted MCP input mappings do not match the resolved descriptor.");
        var issues = WorkflowNodeDescriptorConformance.ValidateResolved(node);
        if (!issues.IsEmpty)
            return new(WorkflowNodeDescriptorResolutionKind.MalformedConfiguration, null,
                string.Join("; ", issues.Select(issue => issue.Message)));
        return new(WorkflowNodeDescriptorResolutionKind.Resolved, descriptor,
            "Resolved from persisted node configuration.");
    }

    private static bool AtLeast(string actual, string minimum) =>
        int.TryParse(actual.Split('.').LastOrDefault(), out var actualMinor) &&
        int.TryParse(minimum.Split('.').LastOrDefault(), out var minimumMinor) && actualMinor >= minimumMinor;

    /// <summary>
    /// Resolves persisted direct-MCP argument pins through the same descriptor used by
    /// editor and validator projections. A malformed or incomplete mapping fails closed.
    /// </summary>
    public static bool TryResolveMcpArguments(NodeDefinition node,
        out ImmutableArray<WorkflowMcpArgumentDescriptor> arguments)
    {
        ArgumentNullException.ThrowIfNull(node);
        arguments = [];
        if (node.Type != NodeTypes.McpTool || node.McpInputPorts is not { } persisted)
            return true;
        if (!TryResolve(node, null, null, out var descriptor)) return false;

        var inputPorts = descriptor.Ports
            .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)
            .ToArray();
        var resolved = ImmutableArray.CreateBuilder<WorkflowMcpArgumentDescriptor>(inputPorts.Length);
        foreach (var port in inputPorts)
        {
            var matches = persisted.Where(argument => argument is not null && argument.Id == port.Id).ToArray();
            if (matches.Length != 1 || matches[0].DataType != port.DataType)
                return false;
            resolved.Add(new(port, matches[0]));
        }

        if (resolved.Count != persisted.Length ||
            resolved.Select(item => item.Argument.Name).Distinct(StringComparer.Ordinal).Count() != resolved.Count)
            return false;
        arguments = resolved.ToImmutable();
        return true;
    }

    public static bool TryResolve(NodeDefinition node,
        ImmutableArray<McpInputPortDefinition>? discoveredMcpInputPorts,
        IReadOnlyDictionary<string, string>? discoveredMcpInputDescriptions,
        out WorkflowNodeDescriptor descriptor)
    {
        if (!WorkflowNodeContractCatalog.TryGet(node.Type, out var contract) ||
            !string.Equals(node.TypeVersion, contract.TypeVersion, StringComparison.Ordinal))
        {
            descriptor = null!;
            return false;
        }

        if (WorkflowNodeDescriptorRegistry.TryGetForNode(node, out descriptor))
            return true;

        var effectiveNode = node.Type == NodeTypes.McpTool && discoveredMcpInputPorts.HasValue && node.McpInputPorts is null
            ? node with { McpInputPorts = discoveredMcpInputPorts.Value }
            : node;
        var ports = new List<WorkflowPortDescriptor>();
        var mcpInputs = effectiveNode.McpInputPorts;

        foreach (var port in WorkflowPortShapeCatalog.ExplicitDataInputs(effectiveNode))
        {
            var mcpPort = effectiveNode.Type == NodeTypes.McpTool
                ? mcpInputs?.FirstOrDefault(candidate => candidate is not null && candidate.Id == port.Id)
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

        foreach (var port in WorkflowPortShapeCatalog.ExplicitDataOutputs(effectiveNode))
            ports.Add(new WorkflowPortDescriptor(port.Id, WorkflowPortDirection.Output, WorkflowPortKind.Data, port.DataType)
            {
                Cardinality = WorkflowPortCardinality.Many
            });

        foreach (var port in WorkflowPortShapeCatalog.ExplicitExecutionInputs(effectiveNode))
            ports.Add(new WorkflowPortDescriptor(port, WorkflowPortDirection.Input, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            });

        foreach (var port in WorkflowPortShapeCatalog.ExplicitExecutionOutputs(effectiveNode))
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
        NodeTypes.VariableGet => "variable-get",
        NodeTypes.VariableSet => "variable-set",
        NodeTypes.FunctionCall => "graph-function-call",
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous => "parallel",
        NodeTypes.Sequence => "sequence",
        _ => "configured"
    };
}
