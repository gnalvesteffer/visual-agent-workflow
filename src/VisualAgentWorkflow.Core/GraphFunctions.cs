using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>An immutable, deterministic data-flow function body with a portable typed interface.</summary>
public sealed record GraphFunctionDefinition(
    string FunctionId,
    int Version,
    string DisplayName,
    string Description,
    ImmutableSortedDictionary<string, GraphFunctionPort> Inputs,
    ImmutableSortedDictionary<string, GraphFunctionPort> Outputs,
    ImmutableSortedDictionary<string, NodeDefinition> Nodes,
    ImmutableSortedDictionary<string, ConnectionDefinition> Connections);

public sealed record GraphFunctionPort(string DisplayName, string DataType);
public sealed record GraphFunctionConfiguration(GraphFunctionDefinition Definition, string ContentHash);
public sealed record GraphFunctionEvaluation(string DataType, object Value, ImmutableArray<GraphFunctionValueEvidence> Values);
public sealed record GraphFunctionValueEvidence(string NodeId, string PortId, string DataType, object Value);

public static class GraphFunctionLimits
{
    public const int MaximumNodes = 64;
    public const int MaximumConnections = 128;
    public const int MaximumInputs = 16;
    public const int MaximumOutputs = 16;
    public const int MaximumIdLength = 128;
    public const int MaximumDisplayNameLength = 160;
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumStringLength = 16_384;
    public const int MaximumRunNodeVisits = 1_024;
}

/// <summary>Portable graph-function hashing, validation, and deterministic evaluation.</summary>
public static class GraphFunctionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static string ComputeHash(GraphFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(definition, Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string Serialize(GraphFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, Options);
    }

    public static GraphFunctionDefinition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonDocumentSafety.RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<GraphFunctionDefinition>(json, Options)
            ?? throw new JsonException("A graph function definition is required.");
    }

    public static ImmutableArray<ValidationIssue> Validate(GraphFunctionDefinition? function)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));
        if (function is null)
        {
            Error("function.missing", "graphFunction.definition", "An immutable graph function definition is required.");
            return issues.ToImmutable();
        }
        if (function.Inputs is null || function.Outputs is null || function.Nodes is null || function.Connections is null)
        {
            Error("function.structure", "graphFunction.definition", "Function ports, nodes, and data connections are required.");
            return issues.ToImmutable();
        }

        if (string.IsNullOrWhiteSpace(function.FunctionId) || function.FunctionId.Length > GraphFunctionLimits.MaximumIdLength ||
            function.FunctionId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            Error("function.id", "graphFunction.definition.functionId", "Function IDs must use letters, digits, '.', '_' or '-' and fit within 128 characters.");
        if (function.Version < 1)
            Error("function.version", "graphFunction.definition.version", "A graph function version must be positive.");
        if (string.IsNullOrWhiteSpace(function.DisplayName) || function.DisplayName.Length > GraphFunctionLimits.MaximumDisplayNameLength)
            Error("function.name", "graphFunction.definition.displayName", "A display name of 1–160 characters is required.");
        if (function.Description is null || function.Description.Length > GraphFunctionLimits.MaximumDescriptionLength)
            Error("function.description", "graphFunction.definition.description", "Function descriptions are limited to 2,000 characters.");
        if (function.Inputs.Count is < 1 or > GraphFunctionLimits.MaximumInputs)
            Error("function.limit", "graphFunction.definition.inputs", "Functions require 1–16 named inputs.");
        if (function.Outputs.Count is < 1 or > GraphFunctionLimits.MaximumOutputs)
            Error("function.limit", "graphFunction.definition.outputs", "Functions require 1–16 named outputs.");
        if (function.Nodes.Count is < 1 or > GraphFunctionLimits.MaximumNodes || function.Connections.Count > GraphFunctionLimits.MaximumConnections)
            Error("function.limit", "graphFunction.definition.nodes", "Function bodies allow 1–64 nodes and at most 128 data connections.");

        ValidatePorts(function.Inputs, "inputs", Error);
        ValidatePorts(function.Outputs, "outputs", Error);
        foreach (var (id, node) in function.Nodes)
        {
            if (node is null || node.Type is not (NodeTypes.FunctionInput or NodeTypes.FunctionOutput or NodeTypes.StringConstant or
                NodeTypes.TextConcat or NodeTypes.StringEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot))
            {
                Error("function.node.unsupported", $"graphFunction.definition.nodes.{id}", "Function bodies support only function boundaries and deterministic string/Boolean value nodes.");
                continue;
            }

            if (HasUnexpectedConfiguration(node))
                Error("function.configuration", $"graphFunction.definition.nodes.{id}", "Function nodes may contain only the configuration defined for their exact node type.");

            if (node.Type is NodeTypes.FunctionInput or NodeTypes.FunctionOutput)
            {
                var ports = node.Type == NodeTypes.FunctionInput ? function.Inputs : function.Outputs;
                if (node.FunctionPortId is not { } portId || !ports.ContainsKey(portId))
                    Error("function.port", $"graphFunction.definition.nodes.{id}.functionPortId", "Function boundary nodes must reference a declared port ID.");
            }
            else if (node.FunctionPortId is not null || node.GraphFunction is not null || node.WorkflowCall is not null)
                Error("function.configuration", $"graphFunction.definition.nodes.{id}", "Pure value nodes cannot contain function or workflow call configuration.");

            if (node.Type == NodeTypes.StringConstant && (node.StringConstant is null || node.StringConstant.Value.Length > GraphFunctionLimits.MaximumStringLength))
                Error("function.constant", $"graphFunction.definition.nodes.{id}.stringConstant", "String constants must be configured and limited to 16,384 characters.");
            if (node.Type == NodeTypes.BooleanConstant && node.BooleanConstant is null)
                Error("function.constant", $"graphFunction.definition.nodes.{id}.booleanConstant", "Boolean constants must have an explicit value.");
        }

        foreach (var (id, port) in function.Inputs)
            ValidateBoundary(id, NodeTypes.FunctionInput, "inputs", function, Error);
        foreach (var (id, port) in function.Outputs)
            ValidateBoundary(id, NodeTypes.FunctionOutput, "outputs", function, Error);

        var edges = new HashSet<(string Source, string SourcePort, string Target, string TargetPort)>();
        var incoming = new Dictionary<(string Node, string Port), int>();
        var adjacency = function.Nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (edgeId, edge) in function.Connections)
        {
            var path = $"graphFunction.definition.connections.{edgeId}";
            if (edge is null || edge.Source is null || edge.Target is null ||
                !function.Nodes.TryGetValue(edge.Source.NodeId, out var source) || source is null ||
                !function.Nodes.TryGetValue(edge.Target.NodeId, out var target) || target is null)
            {
                Error("function.edge.node", path, "Data connections must reference existing function nodes.");
                continue;
            }
            var sourcePort = Outputs(source, function).SingleOrDefault(port => port.Id == edge.Source.PortId);
            var targetPort = Inputs(target, function).SingleOrDefault(port => port.Id == edge.Target.PortId);
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || sourcePort.DataType != targetPort.DataType)
                Error("function.edge.type", path, "Function data connections must join declared ports of the same type.");
            if (!edges.Add((edge.Source.NodeId, edge.Source.PortId, edge.Target.NodeId, edge.Target.PortId)))
                Error("function.edge.duplicate", path, "Duplicate function data connections are not allowed.");
            incoming[(edge.Target.NodeId, edge.Target.PortId)] = incoming.GetValueOrDefault((edge.Target.NodeId, edge.Target.PortId)) + 1;
            adjacency[edge.Source.NodeId].Add(edge.Target.NodeId);
        }

        foreach (var (id, node) in function.Nodes)
            foreach (var port in Inputs(node, function))
                if (incoming.GetValueOrDefault((id, port.Id)) != 1)
                    Error("function.input.degree", $"graphFunction.definition.nodes.{id}.inputs.{port.Id}", "Every function data input must have exactly one source.");

        if (HasCycle(adjacency)) Error("function.cycle", "graphFunction.definition.connections", "Function data dependencies must be acyclic.");
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(function.Nodes.Where(pair => pair.Value?.Type == NodeTypes.FunctionOutput).Select(pair => pair.Key));
        var reverse = function.Nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in function.Connections.Values)
            if (reverse.TryGetValue(edge.Target.NodeId, out var predecessors)) predecessors.Add(edge.Source.NodeId);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current)) continue;
            foreach (var dependency in reverse.GetValueOrDefault(current) ?? []) pending.Push(dependency);
        }
        if (function.Nodes.Keys.Any(id => !reachable.Contains(id)))
            Error("function.unused", "graphFunction.definition.nodes", "Every function node must contribute to a declared output.");
        return issues.ToImmutable();
    }

    public static GraphFunctionEvaluation Evaluate(GraphFunctionDefinition function,
        IReadOnlyDictionary<string, GraphFunctionEvaluation> inputs, string outputId,
        Func<int> recordNodeVisit)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(recordNodeVisit);
        if (!function.Outputs.TryGetValue(outputId, out var outputContract))
            throw new InvalidOperationException("The requested function output is not declared.");

        var cache = new Dictionary<(string NodeId, string PortId), GraphFunctionEvaluation>();
        var active = new HashSet<(string NodeId, string PortId)>();
        var values = ImmutableArray.CreateBuilder<GraphFunctionValueEvidence>();
        var outputNode = function.Nodes.Single(pair => pair.Value.Type == NodeTypes.FunctionOutput && pair.Value.FunctionPortId == outputId).Key;
        var edge = function.Connections.Single(pair => pair.Value.Target.NodeId == outputNode && pair.Value.Target.PortId == "in");
        var result = Resolve(edge.Value.Source.NodeId, edge.Value.Source.PortId);
        if (result.DataType != outputContract.DataType)
            throw new InvalidOperationException("The function produced a value that does not match its output contract.");
        return result with { Values = values.ToImmutable() };

        GraphFunctionEvaluation Resolve(string nodeId, string portId)
        {
            var key = (nodeId, portId);
            if (cache.TryGetValue(key, out var cached))
            {
                values.Add(new(nodeId, portId, cached.DataType, cached.Value));
                return cached;
            }
            if (!active.Add(key)) throw new InvalidOperationException("Function data evaluation encountered a cycle.");
            try
            {
                if (!function.Nodes.TryGetValue(nodeId, out var node) || node is null)
                    throw new InvalidOperationException("A function data source is unavailable.");
                var visits = recordNodeVisit();
                if (visits > GraphFunctionLimits.MaximumRunNodeVisits)
                    throw new InvalidOperationException("The run exceeded its 1,024 graph-function node visit limit.");
                var result = node.Type switch
                {
                    NodeTypes.FunctionInput when portId == "out" && node.FunctionPortId is { } inputId && inputs.TryGetValue(inputId, out var inputValue) => inputValue,
                    NodeTypes.StringConstant when portId == "out" => new GraphFunctionEvaluation("string", node.StringConstant!.Value, []),
                    NodeTypes.TextConcat when portId == "out" => new GraphFunctionEvaluation("string", Concatenate(nodeId), []),
                    NodeTypes.StringEquals when portId == "out" => new GraphFunctionEvaluation("boolean",
                        string.Equals(ResolveInput(nodeId, "left", "string").Value as string, ResolveInput(nodeId, "right", "string").Value as string, StringComparison.Ordinal), []),
                    NodeTypes.BooleanConstant when portId == "out" => new GraphFunctionEvaluation("boolean", node.BooleanConstant!.Value, []),
                    NodeTypes.BooleanNot when portId == "out" => new GraphFunctionEvaluation("boolean", !(ResolveInput(nodeId, "value", "boolean").Value as bool? ?? throw new InvalidOperationException("Boolean NOT requires a Boolean input.")), []),
                    _ => throw new InvalidOperationException("The function data source is not a supported pure value node.")
                };
                if (result.DataType == "string" && result.Value is string text && text.Length > GraphFunctionLimits.MaximumStringLength)
                    throw new InvalidOperationException("Function string output exceeds 16,384 characters.");
                cache.Add(key, result);
                values.Add(new(nodeId, portId, result.DataType, result.Value));
                return result;
            }
            finally
            {
                active.Remove(key);
            }
        }

        GraphFunctionEvaluation ResolveInput(string nodeId, string portId, string requiredType)
        {
            var inputEdge = function.Connections.Single(pair => pair.Value.Target.NodeId == nodeId && pair.Value.Target.PortId == portId);
            var value = Resolve(inputEdge.Value.Source.NodeId, inputEdge.Value.Source.PortId);
            if (value.DataType != requiredType) throw new InvalidOperationException($"The function input requires a {requiredType} value.");
            return value;
        }

        string Concatenate(string nodeId)
        {
            var left = ResolveInput(nodeId, "left", "string").Value as string ?? throw new InvalidOperationException("Text concatenation requires strings.");
            var right = ResolveInput(nodeId, "right", "string").Value as string ?? throw new InvalidOperationException("Text concatenation requires strings.");
            if ((long)left.Length + right.Length > GraphFunctionLimits.MaximumStringLength)
                throw new InvalidOperationException("Function text concatenation exceeds 16,384 characters.");
            return string.Concat(left, right);
        }
    }

    private static void ValidatePorts(ImmutableSortedDictionary<string, GraphFunctionPort> ports, string kind,
        Action<string, string, string> error)
    {
        foreach (var (id, port) in ports)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > GraphFunctionLimits.MaximumIdLength ||
                id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                error("function.port.id", $"graphFunction.definition.{kind}.{id}", "Function port IDs must use letters, digits, '_' or '-' and fit within 128 characters.");
            if (port is null || string.IsNullOrWhiteSpace(port.DisplayName) || port.DisplayName.Length > GraphFunctionLimits.MaximumDisplayNameLength)
                error("function.port.name", $"graphFunction.definition.{kind}.{id}.displayName", "Function port names must contain 1–160 characters.");
            if (port is not null && port.DataType is not ("string" or "boolean"))
                error("function.port.type", $"graphFunction.definition.{kind}.{id}.dataType", "This version supports only string and Boolean function ports.");
        }
    }

    private static void ValidateBoundary(string id, string nodeType, string collection,
        GraphFunctionDefinition function, Action<string, string, string> error)
    {
        var nodes = function.Nodes.Where(pair => pair.Value?.Type == nodeType && pair.Value.FunctionPortId == id).ToArray();
        if (nodes.Length != 1)
            error("function.boundary", $"graphFunction.definition.{collection}.{id}", "Each declared function input and output must have exactly one matching boundary node.");
        var boundary = nodes.FirstOrDefault().Value;
        if (boundary is null) return;
    }

    private static ImmutableArray<WorkflowPort> Inputs(NodeDefinition node, GraphFunctionDefinition function) => node.Type switch
    {
        NodeTypes.FunctionOutput when node.FunctionPortId is { } id && function.Outputs.TryGetValue(id, out var output) => [new("in", output.DataType)],
        NodeTypes.TextConcat or NodeTypes.StringEquals => [new("left", "string"), new("right", "string")],
        NodeTypes.BooleanNot => [new("value", "boolean")],
        _ => []
    };

    private static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node, GraphFunctionDefinition function) => node.Type switch
    {
        NodeTypes.FunctionInput when node.FunctionPortId is { } id && function.Inputs.TryGetValue(id, out var input) => [new("out", input.DataType)],
        NodeTypes.StringConstant or NodeTypes.TextConcat => [new("out", "string")],
        NodeTypes.StringEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot => [new("out", "boolean")],
        _ => []
    };

    private static bool HasUnexpectedConfiguration(NodeDefinition node)
    {
        var hasCommonConfiguration = node.Tool is not null || node.Agent is not null || node.Origin is not null ||
            node.Condition is not null || node.Approval is not null || node.Termination is not null || node.ParameterId is not null ||
            node.InputDataType is not null || node.OutputDataType is not null || node.JsonPropertySelection is not null ||
            node.JsonProjection is not null || node.TextTemplate is not null || node.Loop is not null || node.WorkflowCall is not null ||
            node.GraphFunction is not null;
        return hasCommonConfiguration || node.Type switch
        {
            NodeTypes.FunctionInput or NodeTypes.FunctionOutput => node.StringConstant is not null || node.BooleanConstant is not null,
            NodeTypes.StringConstant => node.FunctionPortId is not null || node.BooleanConstant is not null,
            NodeTypes.BooleanConstant => node.FunctionPortId is not null || node.StringConstant is not null,
            _ => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null
        };
    }

    private static bool HasCycle(IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        return adjacency.Keys.Any(Visit);

        bool Visit(string node)
        {
            if (active.Contains(node)) return true;
            if (!visited.Add(node)) return false;
            active.Add(node);
            var hasCycle = adjacency[node].Any(Visit);
            active.Remove(node);
            return hasCycle;
        }
    }
}
