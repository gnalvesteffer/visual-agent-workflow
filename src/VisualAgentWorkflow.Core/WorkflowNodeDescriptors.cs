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
        Descriptor(NodeTypes.Uppercase, "uppercase", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.Reverse, "reverse", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.StringConstant, "string-constant", [Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.BooleanConstant, "boolean-constant", [Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.IntegerConstant, "integer-constant", [Output(WorkflowDataTypes.Integer)]),
        Descriptor(NodeTypes.NumberConstant, "number-constant", [Output(WorkflowDataTypes.Number)]),
        Descriptor(NodeTypes.NumberAdd, "number-add", [Input("left", WorkflowDataTypes.Number), Input("right", WorkflowDataTypes.Number), Output(WorkflowDataTypes.Number)]),
        Descriptor(NodeTypes.StringToInteger, "string-to-integer", [Input("in", WorkflowDataTypes.String), Output(WorkflowDataTypes.Integer)]),
        Descriptor(NodeTypes.TextConcat, "text-concat", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.String)]),
        Descriptor(NodeTypes.StringEquals, "string-equals", [Input("left", WorkflowDataTypes.String), Input("right", WorkflowDataTypes.String), Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.IntegerAdd, "integer-add", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Integer)]),
        Descriptor(NodeTypes.IntegerEquals, "integer-equals", [Input("left", WorkflowDataTypes.Integer), Input("right", WorkflowDataTypes.Integer), Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.BooleanNot, "boolean-not", [Input("value", WorkflowDataTypes.Boolean), Output(WorkflowDataTypes.Boolean)]),
        Descriptor(NodeTypes.BooleanBranch, "boolean-branch", [
            Input("condition", WorkflowDataTypes.Boolean),
            ExecutionInput("execute"),
            ExecutionOutput("true"),
            ExecutionOutput("false")])
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

        foreach (var port in WorkflowPorts.ExplicitDataInputs(effectiveNode))
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
        NodeTypes.VariableGet => "variable-get",
        NodeTypes.VariableSet => "variable-set",
        NodeTypes.FunctionCall => "graph-function-call",
        NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous => "parallel",
        NodeTypes.Sequence => "sequence",
        _ => "configured"
    };
}
