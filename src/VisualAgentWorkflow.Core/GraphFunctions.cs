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
public sealed record GraphFunctionCatalogEntry(GraphFunctionConfiguration Function, string Folder);

/// <summary>Authoring catalog used to select immutable graph-function revisions; execution uses embedded snapshots.</summary>
public interface IGraphFunctionCatalog
{
    IReadOnlyList<GraphFunctionCatalogEntry> List();
}

/// <summary>Optional authoring capability for persisting immutable graph-function revisions.</summary>
public interface IGraphFunctionAuthoringCatalog : IGraphFunctionCatalog
{
    GraphFunctionCatalogEntry SaveNewVersion(GraphFunctionDefinition definition, string folder, WorkflowLayout layout);
}

public sealed record GraphFunctionEvaluation(string DataType, object Value, ImmutableArray<GraphFunctionValueEvidence> Values);
public sealed record GraphFunctionValueEvidence(string NodeId, string PortId, string DataType, object Value);

/// <summary>Bounds array values retained across one logical graph-function invocation.</summary>
public sealed class GraphFunctionEvaluationBudget
{
    public const int MaximumAggregateArrayBytes = 65_536;
    private long retainedArrayBytes;

    /// <summary>Number of canonical array bytes retained so far.</summary>
    public long RetainedArrayBytes => Interlocked.Read(ref retainedArrayBytes);

    /// <summary>Charges an array result retained outside the evaluator, such as an output or tool event.</summary>
    public void RecordOutput(string dataType, object value) => Record(dataType, value);

    internal void Record(string dataType, object value)
    {
        if (!WorkflowDataTypes.TryGetArrayElementType(dataType, out var elementType) || elementType == WorkflowDataTypes.Json)
            return;

        if (value is not JsonElement json || json.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Graph function array value must be a valid {dataType} value.");
        if (!WorkflowArrayLiteral.TryNormalizeValue(elementType, json.GetRawText(), out var normalized, out var error))
            throw new InvalidOperationException($"Graph function array value must be a valid {dataType} value: {error}");

        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        var total = Interlocked.Add(ref retainedArrayBytes, byteCount);
        if (total > MaximumAggregateArrayBytes)
        {
            Interlocked.Add(ref retainedArrayBytes, -byteCount);
            throw new InvalidOperationException($"Graph function array values and evidence exceed the {MaximumAggregateArrayBytes}-byte aggregate limit.");
        }
    }
}

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

    public static ImmutableArray<ValidationIssue> Validate(GraphFunctionDefinition? function, bool allowNumber = true, bool allowNumberAdd = false)
        => Validate(function, allowNumber, allowNumberAdd, allowPrimitiveArrays: false);

    public static ImmutableArray<ValidationIssue> Validate(GraphFunctionDefinition? function, bool allowNumber,
        bool allowNumberAdd, bool allowPrimitiveArrays)
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

        ValidatePorts(function.Inputs, "inputs", Error, allowNumber, allowPrimitiveArrays);
        ValidatePorts(function.Outputs, "outputs", Error, allowNumber, allowPrimitiveArrays);
        foreach (var (id, node) in function.Nodes)
        {
            if (node is null)
            {
                Error("function.node.unsupported", $"graphFunction.definition.nodes.{id}", "Function bodies require non-null node definitions.");
                continue;
            }
            var currentNode = node;
            var supportedNode = currentNode.Type is (NodeTypes.FunctionInput or NodeTypes.FunctionOutput or NodeTypes.StringConstant or
                NodeTypes.TextConcat or NodeTypes.StringEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot or NodeTypes.IntegerConstant or NodeTypes.IntegerAdd or NodeTypes.IntegerEquals);
            supportedNode |= allowNumber && (currentNode.Type == NodeTypes.NumberConstant || allowNumberAdd && currentNode.Type == NodeTypes.NumberAdd);
            supportedNode |= allowPrimitiveArrays && currentNode.Type is (NodeTypes.ArrayLiteral or NodeTypes.ArrayLength or NodeTypes.ArrayGet);
            if (!supportedNode)
            {
                Error("function.node.unsupported", $"graphFunction.definition.nodes.{id}", "Function bodies support only function boundaries and deterministic string, Boolean, integer, finite number, and enabled bounded-array value nodes.");
                continue;
            }

            if (HasUnexpectedConfiguration(currentNode))
                Error("function.configuration", $"graphFunction.definition.nodes.{id}", "Function nodes may contain only the configuration defined for their exact node type.");

            if (currentNode.Type is NodeTypes.FunctionInput or NodeTypes.FunctionOutput)
            {
                var ports = currentNode.Type == NodeTypes.FunctionInput ? function.Inputs : function.Outputs;
                if (currentNode.FunctionPortId is not { } portId || !ports.ContainsKey(portId))
                    Error("function.port", $"graphFunction.definition.nodes.{id}.functionPortId", "Function boundary nodes must reference a declared port ID.");
                if (currentNode.Type == NodeTypes.FunctionInput && currentNode.OutputDataType is { } inputType &&
                    (!function.Inputs.TryGetValue(currentNode.FunctionPortId ?? "", out var inputPort) || inputPort.DataType != inputType))
                    Error("function.port.type", $"graphFunction.definition.nodes.{id}.outputDataType", "Function input boundary type must match its declared interface pin.");
                if (currentNode.Type == NodeTypes.FunctionOutput && currentNode.InputDataType is { } outputType &&
                    (!function.Outputs.TryGetValue(currentNode.FunctionPortId ?? "", out var outputPort) || outputPort.DataType != outputType))
                    Error("function.port.type", $"graphFunction.definition.nodes.{id}.inputDataType", "Function output boundary type must match its declared interface pin.");
            }
            else if (currentNode.FunctionPortId is not null || currentNode.GraphFunction is not null || currentNode.WorkflowCall is not null)
                Error("function.configuration", $"graphFunction.definition.nodes.{id}", "Pure value nodes cannot contain function or workflow call configuration.");

            if (currentNode.Type == NodeTypes.StringConstant && (currentNode.StringConstant is null || currentNode.StringConstant.Value.Length > GraphFunctionLimits.MaximumStringLength))
                Error("function.constant", $"graphFunction.definition.nodes.{id}.stringConstant", "String constants must be configured and limited to 16,384 characters.");
            if (currentNode.Type == NodeTypes.BooleanConstant && currentNode.BooleanConstant is null)
                Error("function.constant", $"graphFunction.definition.nodes.{id}.booleanConstant", "Boolean constants must have an explicit value.");
            if (currentNode.Type == NodeTypes.IntegerConstant && currentNode.IntegerConstant is null)
                Error("function.constant", $"graphFunction.definition.nodes.{id}.integerConstant", "Integer constants must have an explicit signed 64-bit value.");
            if (currentNode.Type == NodeTypes.NumberConstant && (currentNode.NumberConstant is not { Value: var number } || !double.IsFinite(number)))
                Error("function.constant", $"graphFunction.definition.nodes.{id}.numberConstant", "Number constants must have an explicit finite IEEE-754 double value.");
            if (currentNode.Type == NodeTypes.ArrayLiteral)
            {
                if (!allowPrimitiveArrays)
                    Error("function.array.literal", $"graphFunction.definition.nodes.{id}.arrayLiteral", "Typed array nodes require the unified schema 2.0 graph.");
                else if (currentNode.ArrayLiteral is not { } arrayLiteral ||
                         !WorkflowDataTypes.IsPrimitiveArrayType(WorkflowDataTypes.ArrayOf(arrayLiteral.ElementType)) ||
                         !WorkflowArrayLiteral.TryParse(arrayLiteral, out _, out var arrayError))
                    Error("function.array.literal", $"graphFunction.definition.nodes.{id}.arrayLiteral", "Array literals require a supported bounded primitive element type and valid values.");
            }
            if (currentNode.Type is NodeTypes.ArrayLength or NodeTypes.ArrayGet &&
                (!allowPrimitiveArrays || !WorkflowDataTypes.IsPrimitiveArrayType(WorkflowDataTypes.ArrayOf(currentNode.ArrayElementType ?? ""))))
                Error("function.array.type", $"graphFunction.definition.nodes.{id}.arrayElementType", "Function array operations require a supported primitive array element type in schema 2.0.");
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
        => Evaluate(function, inputs, outputId, recordNodeVisit, new GraphFunctionEvaluationBudget());

    public static GraphFunctionEvaluation Evaluate(GraphFunctionDefinition function,
        IReadOnlyDictionary<string, GraphFunctionEvaluation> inputs, string outputId,
        Func<int> recordNodeVisit, GraphFunctionEvaluationBudget budget)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(recordNodeVisit);
        ArgumentNullException.ThrowIfNull(budget);
        if (!function.Outputs.TryGetValue(outputId, out var outputContract))
            throw new InvalidOperationException("The requested function output is not declared.");

        var normalizedInputs = new Dictionary<string, GraphFunctionEvaluation>(StringComparer.Ordinal);
        foreach (var (inputId, inputContract) in function.Inputs)
        {
            if (!inputs.TryGetValue(inputId, out var input) || input.DataType != inputContract.DataType)
                throw new InvalidOperationException($"Graph function input '{inputId}' requires a value of type {inputContract.DataType}.");
            if (WorkflowDataTypes.TryGetArrayElementType(inputContract.DataType, out var elementType) && elementType != WorkflowDataTypes.Json)
            {
                budget.Record(input.DataType, input.Value);
                if (input.Value is not JsonElement json || !TryNormalizeArray(elementType, json.GetRawText(), out var normalized))
                    throw new InvalidOperationException($"Graph function input '{inputId}' is not a valid {inputContract.DataType} value.");
                input = input with { Value = normalized };
            }
            normalizedInputs.Add(inputId, input);
        }
        if (inputs.Keys.Any(key => !function.Inputs.ContainsKey(key)))
            throw new InvalidOperationException("The graph function received an undeclared input.");

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
                if (cached.DataType.StartsWith("array<", StringComparison.Ordinal)) budget.Record(cached.DataType, cached.Value);
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
                    NodeTypes.FunctionInput when portId == "out" && node.FunctionPortId is { } inputId && normalizedInputs.TryGetValue(inputId, out var inputValue) => inputValue,
                    NodeTypes.StringConstant when portId == "out" => new GraphFunctionEvaluation("string", node.StringConstant!.Value, []),
                    NodeTypes.TextConcat when portId == "out" => new GraphFunctionEvaluation("string", Concatenate(nodeId), []),
                    NodeTypes.StringEquals when portId == "out" => new GraphFunctionEvaluation("boolean",
                        string.Equals(ResolveInput(nodeId, "left", "string").Value as string, ResolveInput(nodeId, "right", "string").Value as string, StringComparison.Ordinal), []),
                    NodeTypes.IntegerConstant when portId == "out" => new GraphFunctionEvaluation("integer", node.IntegerConstant!.Value, []),
                    NodeTypes.NumberConstant when portId == "out" && node.NumberConstant is { Value: var number } && double.IsFinite(number) => new GraphFunctionEvaluation("number", number, []),
                    NodeTypes.NumberAdd when portId == "out" => new GraphFunctionEvaluation("number", AddNumbers(nodeId), []),
                    NodeTypes.IntegerAdd when portId == "out" => new GraphFunctionEvaluation("integer", AddIntegers(nodeId), []),
                    NodeTypes.IntegerEquals when portId == "out" => new GraphFunctionEvaluation("boolean", IntegersEqual(nodeId), []),
                    NodeTypes.BooleanConstant when portId == "out" => new GraphFunctionEvaluation("boolean", node.BooleanConstant!.Value, []),
                    NodeTypes.BooleanNot when portId == "out" => new GraphFunctionEvaluation("boolean", !(ResolveInput(nodeId, "value", "boolean").Value as bool? ?? throw new InvalidOperationException("Boolean NOT requires a Boolean input.")), []),
                    NodeTypes.ArrayLiteral when portId == "out" => EvaluateArrayLiteral(node),
                    NodeTypes.ArrayLength when portId == "out" => new GraphFunctionEvaluation("integer",
                        (long)ResolveArrayInput(nodeId, "in", node.ArrayElementType!).GetArrayLength(), []),
                    NodeTypes.ArrayGet when portId == "item" => EvaluateArrayGet(nodeId, node.ArrayElementType!),
                    _ => throw new InvalidOperationException("The function data source is not a supported pure value node.")
                };
                if (result.DataType == "string" && result.Value is string text && text.Length > GraphFunctionLimits.MaximumStringLength)
                    throw new InvalidOperationException("Function string output exceeds 16,384 characters.");
                if (result.DataType == "number" && (result.Value is not double finiteNumber || !double.IsFinite(finiteNumber)))
                    throw new InvalidOperationException("Function number values must be finite IEEE-754 doubles.");
                if (result.DataType.StartsWith("array<", StringComparison.Ordinal)) budget.Record(result.DataType, result.Value);
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

        JsonElement ResolveArrayInput(string nodeId, string portId, string elementType)
        {
            var value = ResolveInput(nodeId, portId, WorkflowDataTypes.ArrayOf(elementType));
            if (value.Value is not JsonElement json || json.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Function array input '{portId}' must be a bounded {elementType} array.");
            return json;
        }

        GraphFunctionEvaluation EvaluateArrayLiteral(NodeDefinition node)
        {
            var configuration = node.ArrayLiteral ?? throw new InvalidOperationException("Function array literal configuration is missing.");
            if (!WorkflowArrayLiteral.TryParse(configuration, out var values, out var error))
                throw new InvalidOperationException(error);
            if (!TryNormalizeArray(configuration.ElementType, values.GetRawText(), out var normalized))
                throw new InvalidOperationException("Function array literal could not be normalized.");
            return new(WorkflowDataTypes.ArrayOf(configuration.ElementType), normalized, []);
        }

        GraphFunctionEvaluation EvaluateArrayGet(string nodeId, string elementType)
        {
            var array = ResolveArrayInput(nodeId, "array", elementType);
            var index = ResolveInput(nodeId, "index", WorkflowDataTypes.Integer).Value;
            if (index is not long arrayIndex)
                throw new InvalidOperationException("Array Get requires a signed 64-bit integer index.");
            if (arrayIndex < 0 || arrayIndex >= array.GetArrayLength())
                throw new GraphFunctionEvaluationException(nodeId,
                    $"Array index {arrayIndex} is outside an array containing {array.GetArrayLength()} items.");
            var item = array[(int)arrayIndex];
            object value = elementType switch
            {
                WorkflowDataTypes.String => item.GetString()!,
                WorkflowDataTypes.Boolean => item.GetBoolean(),
                WorkflowDataTypes.Integer => item.GetInt64(),
                WorkflowDataTypes.Number => item.GetDouble(),
                _ => throw new InvalidOperationException("Array Get requires a supported primitive element type.")
            };
            return new(elementType, value, []);
        }

        string Concatenate(string nodeId)
        {
            var left = ResolveInput(nodeId, "left", "string").Value as string ?? throw new InvalidOperationException("Text concatenation requires strings.");
            var right = ResolveInput(nodeId, "right", "string").Value as string ?? throw new InvalidOperationException("Text concatenation requires strings.");
            if ((long)left.Length + right.Length > GraphFunctionLimits.MaximumStringLength)
                throw new InvalidOperationException("Function text concatenation exceeds 16,384 characters.");
            return string.Concat(left, right);
        }

        long AddIntegers(string nodeId)
        {
            var left = ResolveInput(nodeId, "left", "integer").Value;
            var right = ResolveInput(nodeId, "right", "integer").Value;
            if (left is not long leftInteger || right is not long rightInteger)
                throw new InvalidOperationException("Integer addition requires Int64 inputs.");
            return checked(leftInteger + rightInteger);
        }

        double AddNumbers(string nodeId)
        {
            var left = ResolveInput(nodeId, "left", "number").Value;
            var right = ResolveInput(nodeId, "right", "number").Value;
            if (left is not double leftNumber || !double.IsFinite(leftNumber) || right is not double rightNumber || !double.IsFinite(rightNumber))
                throw new InvalidOperationException("Number addition requires finite IEEE-754 double inputs.");
            var result = leftNumber + rightNumber;
            if (!double.IsFinite(result))
                throw new InvalidOperationException("Number addition overflowed to a non-finite result.");
            return result;
        }

        bool IntegersEqual(string nodeId)
        {
            var left = ResolveInput(nodeId, "left", "integer").Value;
            var right = ResolveInput(nodeId, "right", "integer").Value;
            if (left is not long leftInteger || right is not long rightInteger)
                throw new InvalidOperationException("Integer equality requires Int64 inputs.");
            return leftInteger == rightInteger;
        }
    }

    private static void ValidatePorts(ImmutableSortedDictionary<string, GraphFunctionPort> ports, string kind,
        Action<string, string, string> error, bool allowNumber, bool allowPrimitiveArrays)
    {
        foreach (var (id, port) in ports)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > GraphFunctionLimits.MaximumIdLength ||
                id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                error("function.port.id", $"graphFunction.definition.{kind}.{id}", "Function port IDs must use letters, digits, '_' or '-' and fit within 128 characters.");
            if (port is null || string.IsNullOrWhiteSpace(port.DisplayName) || port.DisplayName.Length > GraphFunctionLimits.MaximumDisplayNameLength)
                error("function.port.name", $"graphFunction.definition.{kind}.{id}.displayName", "Function port names must contain 1–160 characters.");
            var primitiveArray = port is not null && WorkflowDataTypes.IsPrimitiveArrayType(port.DataType);
            if (port is not null && (port.DataType is not ("string" or "boolean" or "integer") && !(allowNumber && port.DataType == "number") && !(allowPrimitiveArrays && primitiveArray)))
                error("function.port.type", $"graphFunction.definition.{kind}.{id}.dataType", "This function contract supports string, Boolean, signed 64-bit integer, finite number, and—when enabled—bounded primitive-array ports.");
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
        NodeTypes.FunctionOutput when node.FunctionPortId is { } id && function.Outputs.TryGetValue(id, out var output) => [new("in", node.InputDataType ?? output.DataType)],
        NodeTypes.TextConcat or NodeTypes.StringEquals => [new("left", "string"), new("right", "string")],
        NodeTypes.IntegerAdd or NodeTypes.IntegerEquals => [new("left", "integer"), new("right", "integer")],
        NodeTypes.NumberAdd => [new("left", "number"), new("right", "number")],
        NodeTypes.ArrayLength => [new("in", WorkflowDataTypes.ArrayOf(node.ArrayElementType ?? WorkflowDataTypes.Json))],
        NodeTypes.ArrayGet => [new("array", WorkflowDataTypes.ArrayOf(node.ArrayElementType ?? WorkflowDataTypes.Json)), new("index", WorkflowDataTypes.Integer)],
        NodeTypes.ArrayLiteral => [],
        NodeTypes.NumberConstant => [],
        NodeTypes.BooleanNot => [new("value", "boolean")],
        _ => []
    };

    private static ImmutableArray<WorkflowPort> Outputs(NodeDefinition node, GraphFunctionDefinition function) => node.Type switch
    {
        NodeTypes.FunctionInput when node.FunctionPortId is { } id && function.Inputs.TryGetValue(id, out var input) => [new("out", node.OutputDataType ?? input.DataType)],
        NodeTypes.StringConstant or NodeTypes.TextConcat => [new("out", "string")],
        NodeTypes.IntegerConstant or NodeTypes.IntegerAdd => [new("out", "integer")],
        NodeTypes.NumberConstant or NodeTypes.NumberAdd => [new("out", "number")],
        NodeTypes.ArrayLiteral when node.ArrayLiteral is { } literal => [new("out", WorkflowDataTypes.ArrayOf(literal.ElementType))],
        NodeTypes.ArrayLength => [new("out", WorkflowDataTypes.Integer)],
        NodeTypes.ArrayGet => [new("item", node.ArrayElementType ?? WorkflowDataTypes.Json)],
        NodeTypes.StringEquals or NodeTypes.IntegerEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot => [new("out", "boolean")],
        _ => []
    };

    private static bool HasUnexpectedConfiguration(NodeDefinition node)
    {
        var hasCommonConfiguration = node.Tool is not null || node.Agent is not null || node.Origin is not null ||
            node.Condition is not null || node.Approval is not null || node.Termination is not null || node.ParameterId is not null ||
            node.JsonPropertySelection is not null ||
            node.JsonProjection is not null || node.TextTemplate is not null || node.Loop is not null || node.WorkflowCall is not null ||
            node.GraphFunction is not null;
        return hasCommonConfiguration || node.Type switch
        {
            NodeTypes.FunctionInput => node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.ArrayLiteral is not null || node.ArrayElementType is not null,
            NodeTypes.FunctionOutput => node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.OutputDataType is not null || node.ArrayLiteral is not null || node.ArrayElementType is not null,
            NodeTypes.StringConstant => node.FunctionPortId is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null,
            NodeTypes.BooleanConstant => node.FunctionPortId is not null || node.StringConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null,
            NodeTypes.IntegerConstant => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null,
            NodeTypes.NumberConstant => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.InputDataType is not null || node.OutputDataType is not null,
            NodeTypes.ArrayLiteral => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null || node.ArrayElementType is not null,
            NodeTypes.ArrayLength or NodeTypes.ArrayGet => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null || node.ArrayLiteral is not null,
            _ => node.FunctionPortId is not null || node.StringConstant is not null || node.BooleanConstant is not null || node.IntegerConstant is not null || node.NumberConstant is not null || node.InputDataType is not null || node.OutputDataType is not null
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

    private static bool TryNormalizeArray(string elementType, string json, out JsonElement normalized)
    {
        normalized = default;
        if (!WorkflowArrayLiteral.TryNormalizeValue(elementType, json, out var canonical, out _)) return false;
        using var document = JsonDocument.Parse(canonical);
        normalized = document.RootElement.Clone();
        return true;
    }
}

public sealed class GraphFunctionEvaluationException(string nodeId, string message) : InvalidOperationException(message)
{
    public string NodeId { get; } = nodeId;
}
