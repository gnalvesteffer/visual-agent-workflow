using System.Collections.Immutable;
using Microsoft.Agents.AI.Workflows;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed record WorkflowExecutionResult(string? Output, bool Terminated, bool Failed = false);

/// <summary>Translates the supported string workflow into actual Agent Framework executors and edges.</summary>
public sealed partial class WorkflowRunner(INodeToolBroker? tools = null, IAgentNodeExecutor? agents = null)
{
    public async Task<string> ExecuteAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default) =>
        (await ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken)).Output ?? string.Empty;

    public async Task<string> ExecuteWithParametersAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default) =>
        (await ExecuteCoreAsync(runId, revision, input, parameters, record, null, cancellationToken)).Output ?? string.Empty;

    public Task<WorkflowExecutionResult> ExecuteWithOutcomeAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(runId, revision, input, parameters, record, null, cancellationToken);

    public Task<string> ExecuteControlledAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return MapOutput(ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken));
    }

    public Task<string> ExecuteControlledWithParametersAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        IWorkflowExecutionGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return MapOutput(ExecuteCoreAsync(runId, revision, input, parameters, record, gate, cancellationToken));
    }

    public Task<WorkflowExecutionResult> ExecuteControlledWithParametersAndOutcomeAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        IWorkflowExecutionGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return ExecuteCoreAsync(runId, revision, input, parameters, record, gate, cancellationToken);
    }

    private static async Task<string> MapOutput(Task<WorkflowExecutionResult> execution)
    {
        var result = await execution;
        return result.Output ?? string.Empty;
    }

    private async Task<WorkflowExecutionResult> ExecuteCoreAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate? gate, CancellationToken cancellationToken,
        WorkflowRevision? rootRevision = null, ImmutableArray<WorkflowApprovalScope> workflowPath = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        WorkflowValidator.ThrowIfInvalid(revision.Definition);
        var resolvedParameters = WorkflowParameters.Resolve(revision.Definition, parameters);
        rootRevision ??= revision;
        workflowPath = workflowPath.IsDefault ? [] : workflowPath;
        using var recordGate = new SemaphoreSlim(1, 1);
        long sequence = 0;
        async ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null, string? data = null, string? scopeId = null)
        {
            await recordGate.WaitAsync();
            try
            {
                var evt = new ExecutionEvent(runId, revision.RevisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                    kind, node, invocation, connection, data)
                { ScopeId = scopeId };
                await record(evt);
            }
            finally { recordGate.Release(); }
        }

        var workflow = BuildWorkflow(runId, revision, input, resolvedParameters, Emit, gate, rootRevision, workflowPath);

        await Emit(ExecutionEventKinds.Started);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputType = WorkflowPorts.Outputs(revision.Definition.Nodes[revision.Definition.EntryNodeId]).Single().DataType;
            var execution = revision.Definition.Nodes.Values.Any(node => node?.Type == NodeTypes.ParallelFork)
                ? InProcessExecution.Concurrent
                : InProcessExecution.Default;
            await using var run = await execution.RunStreamingAsync(workflow,
                new GraphMessage(input, null, null, DataType: inputType), cancellationToken: cancellationToken);
            GraphMessage? output = null;
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is WorkflowErrorEvent error)
                    throw new InvalidOperationException("Agent Framework execution failed.", error.Exception);
                if (evt is ExecutorFailedEvent failed)
                    throw new InvalidOperationException($"Executor {failed.ExecutorId} failed.", failed.Data);
                if (evt is WorkflowOutputEvent result && result.Data is GraphMessage message)
                    output = message;
            }
            // The framework may finish its event stream normally when the execution token is cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null) throw new InvalidOperationException("The workflow produced no output.");
            if (gate is not null) await gate.WaitForBoundaryAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (output.FailureSignal)
            {
                await Emit(ExecutionEventKinds.Failed, output.SourceNode, data: "Workflow ended at a failure termination.");
                return new(null, true, true);
            }
            if (output.TerminationSignal)
            {
                await Emit(ExecutionEventKinds.Terminated, output.SourceNode);
                return new(null, true);
            }
            await Emit(ExecutionEventKinds.Completed, data: output.Text);
            return new(output.Text, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch
        {
            // Do not copy arbitrary exception messages or provider internals into captured payloads.
            await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw;
        }
    }

    private Workflow BuildWorkflow(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate,
        WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath) =>
        BuildWorkflow(runId, revision, input, parameters, (kind, node, invocation, connection, data, _) => emit(kind, node, invocation, connection, data), gate, rootRevision, workflowPath);

    private Workflow BuildWorkflow(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate,
        WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath)
    {
        var definition = revision.Definition;
        var dataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(definition) ? new ExplicitGraphDataEvaluator(definition, input) : null;
        var executors = definition.Nodes.ToDictionary(
            n => n.Key,
            n => new StringExecutor(n.Key, n.Value, runId, revision, parameters, tools, agents, dataEvaluator, emit, gate, null, rootRevision, workflowPath));
        var builder = new WorkflowBuilder(executors[definition.EntryNodeId]);
        var executionEdges = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? definition.ExecutionConnections!.Values
            : definition.Connections.Values;
        foreach (var edge in executionEdges)
        {
            if (definition.Nodes[edge.Target.NodeId].Type == NodeTypes.ParallelJoin) continue;
            builder.AddEdge<GraphMessage>(executors[edge.Source.NodeId], executors[edge.Target.NodeId], message =>
                message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId);
        }
        foreach (var join in definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.ParallelJoin))
        {
            var branchSources = executionEdges
                .Where(edge => edge.Target.NodeId == join.Key)
                .Select(edge => (ExecutorBinding)executors[edge.Source.NodeId]);
            builder.AddFanInBarrierEdge(branchSources, (ExecutorBinding)executors[join.Key]);
        }
        var outputId = definition.Nodes.Single(n => n.Value.Type == NodeTypes.Output).Key;
        foreach (var terminator in definition.Nodes.Where(n => n.Value.Type is NodeTypes.Terminate or NodeTypes.WorkflowCall))
            builder.AddEdge<GraphMessage>(executors[terminator.Key], executors[outputId], message =>
                message?.SourceNode == terminator.Key && message.SourcePort == "terminated" && message.TerminationSignal);
        builder.WithOutputFrom(executors[outputId]);
        return builder.Build();
    }

    private sealed record GraphMessage(string Text, string? SourceNode, string? SourcePort,
        WorkflowApprovalPermit? Approval = null, string DataType = "string", int LoopIteration = 0,
        bool TerminationSignal = false, bool FailureSignal = false);

    private sealed class AgentRouteSelectionState(ImmutableArray<AgentExecutionRoute> routes)
    {
        private readonly object sync = new();
        private int proposals;
        private string? selectedRouteId;
        private string? invalidReason;

        public bool HasProposed { get { lock (sync) return proposals > 0; } }

        public bool Propose(string routeId)
        {
            lock (sync)
            {
                proposals++;
                if (proposals != 1) invalidReason = "The agent proposed more than one execution route.";
                else if (!routes.Any(route => route.Id == routeId)) invalidReason = "The agent proposed an undeclared execution route.";
                else selectedRouteId = routeId;
                return invalidReason is null;
            }
        }

        public string Commit()
        {
            lock (sync)
            {
                if (proposals == 0) invalidReason = "The agent did not select an execution route.";
                if (invalidReason is not null || selectedRouteId is null)
                    throw new InvalidOperationException($"{invalidReason ?? "The agent did not select exactly one execution route."} The provider must support function calling for routed agents.");
                return selectedRouteId;
            }
        }

        public string RejectionReason
        {
            get { lock (sync) return invalidReason ?? (proposals == 0 ? "No execution route was proposed." : "The proposed route was discarded because the agent did not complete successfully."); }
        }
    }

    private sealed record ExplicitGraphValue(string DataType, object Value);
    private sealed record EvaluatedDataValue(string NodeId, string PortId, string DataType, object Value, string? ConnectionId,
        object? FunctionEvidence = null);
    private sealed record DataInputEvaluation(ExplicitGraphValue Result, ImmutableArray<EvaluatedDataValue> Evaluations);
    private sealed record GraphFunctionAudit(string FunctionId, int Version, string ContentHash, string OutputId,
        IReadOnlyDictionary<string, object> Inputs, ImmutableArray<GraphFunctionValueEvidence> EvaluatedValues);

    /// <summary>Evaluates only bounded, pure explicit-graph data nodes in one activation context.</summary>
    private sealed class ExplicitGraphDataEvaluator(WorkflowDefinition definition, string input)
    {
        private int functionNodeVisits;
        private readonly Dictionary<(string NodeId, string PortId), GraphFunctionAudit> functionAudits = [];
        private readonly Dictionary<(string NodeId, string PortId), ExplicitGraphValue> runtimeValues = [];

        public void SetRuntimeOutput(string nodeId, string portId, string dataType, object value) =>
            runtimeValues[(nodeId, portId)] = new(dataType, value);

        public DataInputEvaluation ResolveInput(string nodeId, string inputPort)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == inputPort);
            var evaluated = ImmutableArray.CreateBuilder<EvaluatedDataValue>();
            var cache = new Dictionary<(string NodeId, string PortId), ExplicitGraphValue>();
            var active = new HashSet<(string NodeId, string PortId)>();
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            return new(value, evaluated.ToImmutable());
        }

        private ExplicitGraphValue ResolveOutput(string nodeId, string outputPort,
            string? connectionId,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var key = (nodeId, outputPort);
            if (cache.TryGetValue(key, out var cached))
            {
                evaluated.Add(new(nodeId, outputPort, cached.DataType, cached.Value, connectionId));
                return cached;
            }
            if (!active.Add(key)) throw new InvalidOperationException("Pure data evaluation encountered a cycle.");
            try
            {
                if (!definition.Nodes.TryGetValue(nodeId, out var node) || node is null)
                    throw new InvalidOperationException("Pure data source is unavailable.");
                if (runtimeValues.TryGetValue(key, out var runtimeValue))
                {
                    cache.Add(key, runtimeValue);
                    evaluated.Add(new(nodeId, outputPort, runtimeValue.DataType, runtimeValue.Value, connectionId));
                    return runtimeValue;
                }
                var result = node.Type switch
                {
                    NodeTypes.Input when outputPort == "out" => new ExplicitGraphValue("string", input),
                    NodeTypes.StringConstant when outputPort == "out" => new ExplicitGraphValue("string", node.StringConstant!.Value),
                    NodeTypes.TextConcat when outputPort == "out" => new ExplicitGraphValue("string", Concatenate(nodeId, "left", "right", evaluated, cache, active)),
                    NodeTypes.StringEquals when outputPort == "out" => new ExplicitGraphValue("boolean",
                        string.Equals(ResolveStringInput(nodeId, "left", evaluated, cache, active),
                            ResolveStringInput(nodeId, "right", evaluated, cache, active), StringComparison.Ordinal)),
                    NodeTypes.BooleanConstant when outputPort == "out" => new ExplicitGraphValue("boolean",
                        node.BooleanConstant?.Value ?? throw new InvalidOperationException("The Boolean constant is not configured.")),
                    NodeTypes.BooleanNot when outputPort == "out" => new ExplicitGraphValue("boolean",
                        !ResolveBooleanInput(nodeId, "value", evaluated, cache, active)),
                    NodeTypes.FunctionCall when node.GraphFunction is { } function && function.Definition.Outputs.TryGetValue(outputPort, out var output) =>
                        EvaluateFunction(nodeId, outputPort, output.DataType, function, evaluated, cache, active),
                    _ => throw new InvalidOperationException("The data source is not a supported pure value node.")
                };
                if (result.DataType == "string" && ((string)result.Value).Length > 16_384)
                    throw new InvalidOperationException("Pure data output exceeds 16,384 characters.");
                cache.Add(key, result);
                functionAudits.TryGetValue(key, out var functionEvidence);
                evaluated.Add(new(nodeId, outputPort, result.DataType, result.Value, connectionId, functionEvidence));
                return result;
            }
            finally
            {
                active.Remove(key);
            }
        }

        private ExplicitGraphValue EvaluateFunction(string nodeId, string outputPort, string outputType,
            GraphFunctionConfiguration configuration,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var function = configuration.Definition;
            var inputs = function.Inputs.ToDictionary(pair => pair.Key,
                pair => ResolveFunctionInput(nodeId, pair.Key, pair.Value.DataType, evaluated, cache, active), StringComparer.Ordinal);
            var result = GraphFunctionJson.Evaluate(function, inputs, outputPort, () => Interlocked.Increment(ref functionNodeVisits));
            if (result.DataType != outputType)
                throw new InvalidOperationException("The graph function returned a value with an incompatible output type.");
            functionAudits[(nodeId, outputPort)] = new(function.FunctionId, function.Version, configuration.ContentHash,
                outputPort, inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal), result.Values);
            return new(result.DataType, result.Value);
        }

        private GraphFunctionEvaluation ResolveFunctionInput(string nodeId, string portId, string dataType,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == portId);
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != dataType)
                throw new InvalidOperationException($"Graph function input '{portId}' requires a {dataType} value.");
            return new(value.DataType, value.Value, []);
        }

        private string Concatenate(string nodeId, string leftPort, string rightPort,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var left = ResolveStringInput(nodeId, leftPort, evaluated, cache, active);
            var right = ResolveStringInput(nodeId, rightPort, evaluated, cache, active);
            if ((long)left.Length + right.Length > 16_384)
                throw new InvalidOperationException("Text concatenation exceeds 16,384 characters.");
            return string.Concat(left, right);
        }

        private string ResolveStringInput(string nodeId, string inputPort,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == inputPort);
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != "string" || value.Value is not string text)
                throw new InvalidOperationException("The pure data input requires a string value.");
            return text;
        }

        private bool ResolveBooleanInput(string nodeId, string inputPort,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == inputPort);
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != "boolean" || value.Value is not bool result)
                throw new InvalidOperationException("The pure data input requires a Boolean value.");
            return result;
        }

        public DataInputEvaluation ResolveBranchCondition(string nodeId)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == "condition");
            var evaluated = ImmutableArray.CreateBuilder<EvaluatedDataValue>();
            var cache = new Dictionary<(string NodeId, string PortId), ExplicitGraphValue>();
            var active = new HashSet<(string NodeId, string PortId)>();
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != "boolean" || value.Value is not bool)
                throw new InvalidOperationException("A Boolean Branch requires an actual Boolean value.");
            return new(value, evaluated.ToImmutable());
        }
    }

    private sealed class StringExecutor(
        string id,
        NodeDefinition node,
        Guid runId,
        WorkflowRevision revision,
        IReadOnlyDictionary<string, string>? parameters,
        INodeToolBroker? tools,
        IAgentNodeExecutor? agents,
        ExplicitGraphDataEvaluator? dataEvaluator,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> record, IWorkflowExecutionGate? gate,
        string? scopeId, WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath)
        // BuildWorkflow creates a fresh executor set for each run; these instances may process branch messages concurrently within that run.
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        private ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null,
            string? data = null, string? eventScope = null) =>
            record(kind, node, invocation, connection, data, eventScope ?? scopeId);

        private static string SerializeDataEvidence(string activatedByNodeId, EvaluatedDataValue value)
        {
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["activatedByNodeId"] = activatedByNodeId,
                ["port"] = value.PortId,
                ["dataType"] = value.DataType,
                ["value"] = value.Value
            };
            if (value.FunctionEvidence is not null) evidence["function"] = value.FunctionEvidence;
            return JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        private sealed class JsonProjectionContractException(string message) : InvalidOperationException(message) { }

        private readonly object parallelJoinSync = new();
        private readonly SemaphoreSlim parallelJoinExecution = new(1, 1);
        private readonly SortedDictionary<string, string> parallelJoinInputs = new(StringComparer.Ordinal);
        private Guid? parallelJoinInvocationId;

        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage received, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var internalTerminationHandoff = node.Type == NodeTypes.Output && received.TerminationSignal;
            if (gate is not null && !internalTerminationHandoff) await gate.WaitForBoundaryAsync(Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var message = received.Text;
            var invocation = node.Type == NodeTypes.ParallelJoin ? ParallelJoinInvocation() : received.Approval?.TargetInvocationId ?? Guid.NewGuid();
            // Receiving the downstream message is direct evidence of delivery, not inferred completion.
            string? incomingPort = null;
            DataInputEvaluation? outputEvaluation = null;
            DataInputEvaluation? branchEvaluation = null;
            var configuredAgentRoutes = node.Type == NodeTypes.Agent ? node.Agent?.Routes : null;
            var routeSelection = configuredAgentRoutes is { IsDefaultOrEmpty: false } routes
                ? new AgentRouteSelectionState(routes)
                : null;
            string? agentRouteId = null;
            if (received.SourceNode is not null && received.TerminationSignal)
            {
                if (node.Type != NodeTypes.Output || !revision.Definition.Nodes.TryGetValue(received.SourceNode, out var sourceNode) ||
                    sourceNode.Type is not (NodeTypes.Terminate or NodeTypes.WorkflowCall))
                    throw new InvalidOperationException("Invalid internal workflow termination signal.");
            }
            else if (received.SourceNode is not null)
            {
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition))
                {
                    var incoming = revision.Definition.ExecutionConnections!.Single(edge => edge.Value.Source.NodeId == received.SourceNode &&
                        edge.Value.Source.PortId == received.SourcePort && edge.Value.Target.NodeId == Id);
                    await Emit(ExecutionEventKinds.ExecutionActivated, Id, invocation, $"execution:{incoming.Key}");
                    incomingPort = incoming.Value.Target.PortId;
                }
                else
                {
                    var incoming = revision.Definition.Connections.Single(edge => edge.Value.Source.NodeId == received.SourceNode &&
                        edge.Value.Source.PortId == received.SourcePort && edge.Value.Target.NodeId == Id);
                    var inputType = WorkflowPorts.Inputs(node).Single(port => port.Id == incoming.Value.Target.PortId).DataType;
                    if (received.DataType != inputType) throw new InvalidOperationException("Connected workflow data type does not match the receiving node.");
                    await Emit(ExecutionEventKinds.Transfer, Id, invocation, incoming.Key, message);
                    incomingPort = incoming.Value.Target.PortId;
                }
            }
            if (node.Type == NodeTypes.ParallelJoin)
            {
                await parallelJoinExecution.WaitAsync(cancellationToken);
                try
                {
                    var (started, complete, result, receivedCount) = CollectParallelJoinInput(incomingPort!, message);
                    if (started) await Emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, "Waiting for both parallel branch results.");
                    await Emit(ExecutionEventKinds.ParallelJoinProgress, Id, invocation, null,
                        complete ? "Both branch results received." : $"Received {receivedCount} of 2 branch results; waiting for the other branch.");
                    if (!complete) return new("", Id, "parallel.pending", DataType: "json");
                    await Emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, result);
                    return new(result!, Id, "out", DataType: "json");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await Emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null, "Parallel join cancelled.");
                    throw;
                }
                catch
                {
                    await Emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, "Parallel join failed.");
                    throw;
                }
                finally { parallelJoinExecution.Release(); }
            }
            if (!internalTerminationHandoff) await Emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message);
            try
            {
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.Output && !internalTerminationHandoff)
                {
                    outputEvaluation = dataEvaluator!.ResolveInput(Id, "in");
                    message = outputEvaluation.Result.DataType switch
                    {
                        "string" when outputEvaluation.Result.Value is string outputValue => outputValue,
                        "json" when outputEvaluation.Result.Value is JsonElement jsonValue && jsonValue.ValueKind == JsonValueKind.Object => jsonValue.GetRawText(),
                        _ => throw new InvalidOperationException("An Output node requires a string or bounded JSON object value.")
                    };
                }
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.BooleanBranch)
                    branchEvaluation = dataEvaluator!.ResolveBranchCondition(Id);
                foreach (var value in (outputEvaluation ?? branchEvaluation)?.Evaluations ?? [])
                    await Emit(ExecutionEventKinds.DataEvaluated, value.NodeId, invocation,
                        value.ConnectionId is null ? null : $"data:{value.ConnectionId}", SerializeDataEvidence(Id, value));
                var childOutcome = node.Type == NodeTypes.WorkflowCall
                    ? await ExecuteWorkflowCall(message, cancellationToken)
                    : null;
                var result = node.Type switch
                {
                    NodeTypes.Input when node.InputDataType == "json" => NormalizeJson(message),
                    NodeTypes.BooleanBranch => string.Empty,
                    NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Approval or NodeTypes.Loop or NodeTypes.LoopBody => message,
                    NodeTypes.Terminate => string.Empty,
                    NodeTypes.JsonSelectString => SelectJsonText(message, node.JsonPropertySelection!.Pointer),
                    NodeTypes.JsonProject => ProjectJson(message, node.JsonProjection!),
                    NodeTypes.ParallelBranch => message,
                    NodeTypes.ParallelFork => message,
                    NodeTypes.TextTemplate => PromptRendering.Render(node.TextTemplate!, message, parameters),
                    NodeTypes.WorkflowValue => parameters is null || !parameters.TryGetValue(node.ParameterId!, out var value)
                        ? throw new InvalidOperationException("The workflow parameter was not resolved for this run.") : value,
                    NodeTypes.Uppercase => message.ToUpperInvariant(),
                    NodeTypes.Reverse => string.Concat(message.EnumerateRunes().Reverse()),
                    NodeTypes.McpTool => await InvokeTool(message, invocation, cancellationToken),
                    NodeTypes.WorkflowCall => childOutcome?.Output ?? string.Empty,
                    NodeTypes.Agent => agents is null ? throw new InvalidOperationException("No agent executor is configured.") :
                        await agents.ExecuteAsync(new NodeInvocationContext(runId, revision, Id, invocation,
                            (kind, data) => Emit(kind, Id, invocation, null, data))
                        {
                            Parameters = parameters,
                            ProposeAgentRoute = routeSelection is null ? null : async routeId =>
                            {
                                await Emit(ExecutionEventKinds.AgentRouteProposed, Id, invocation, null, JsonSerializer.Serialize(new { routeId }));
                                if (!routeSelection.Propose(routeId))
                                    await Emit(ExecutionEventKinds.AgentRouteRejected, Id, invocation, null,
                                        JsonSerializer.Serialize(new { routeId, reason = routeSelection.RejectionReason }));
                            },
                            RecordWithInvocation = (eventInvocationId, kind, data) => Emit(kind, Id, eventInvocationId, null, data)
                        }, message, cancellationToken),
                    _ => throw new InvalidOperationException("Unsupported executor type.")
                };
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.Agent)
                {
                    var structuredResult = node.Agent?.RequireStructuredResult == true;
                    object runtimeValue = structuredResult
                        ? JsonDocument.Parse(result).RootElement.Clone()
                        : result;
                    dataEvaluator!.SetRuntimeOutput(Id, "out", structuredResult ? "json" : "string", runtimeValue);
                }
                if (routeSelection is not null) agentRouteId = routeSelection.Commit();
                WorkflowApprovalPermit? permit = null;
                if (node.Type == NodeTypes.Approval)
                {
                    if (gate is not IWorkflowApprovalGate approvals) throw new InvalidOperationException("No approval coordinator is configured.");
                    var targetId = revision.Definition.Connections.Values.Single(e => e.Source.NodeId == Id).Target.NodeId;
                    var target = revision.Definition.Nodes[targetId];
                    var action = new WorkflowApprovalAction(runId, rootRevision.RevisionId, rootRevision.ContentHash, Id, invocation,
                        targetId, Guid.NewGuid(), target.Tool!, message, WorkflowApprovalAction.HashInput(message))
                    {
                        ExecutingRevisionId = revision.RevisionId == rootRevision.RevisionId ? null : revision.RevisionId,
                        ExecutingWorkflowHash = revision.ContentHash == rootRevision.ContentHash ? null : revision.ContentHash,
                        WorkflowPath = workflowPath.IsEmpty ? null : workflowPath
                    };
                    permit = await approvals.WaitForApprovalAsync(action, node.Approval!.Prompt, cancellationToken);
                    if (permit.TargetInvocationId != action.TargetInvocationId || permit.ActionHash != action.ComputeHash() || permit.RequestId == Guid.Empty)
                        throw new InvalidOperationException("The approval permit does not match the recorded action.");
                }
                var port = agentRouteId is null ? "out" : AgentExecutionRouteLimits.PinId(agentRouteId);
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.Input) port = "then";
                if (node.Type == NodeTypes.BooleanBranch)
                {
                    var condition = branchEvaluation!.Result;
                    port = (bool)condition.Value ? "true" : "false";
                    await Emit(ExecutionEventKinds.BranchSelected, Id, invocation, null, JsonSerializer.Serialize(new { port }));
                }
                if (node.Type == NodeTypes.Condition)
                {
                    port = node.Condition!.Evaluate(message) ? "true" : "false";
                    await Emit(ExecutionEventKinds.BranchSelected, Id, invocation, null, JsonSerializer.Serialize(new { port }));
                }
                var nextLoopIteration = received.LoopIteration;
                if (node.Type == NodeTypes.Loop)
                {
                    var loop = node.Loop!;
                    var shouldContinue = loop.ContinueWhile.Evaluate(result);
                    if (received.LoopIteration > 0)
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, Id, invocation, null, JsonSerializer.Serialize(new { iteration = received.LoopIteration }));
                    if (shouldContinue && received.LoopIteration < loop.MaximumIterations)
                    {
                        port = "body";
                        nextLoopIteration++;
                        await Emit(ExecutionEventKinds.LoopIterationStarted, Id, invocation, null,
                            JsonSerializer.Serialize(new { iteration = nextLoopIteration, maximumIterations = loop.MaximumIterations }));
                    }
                    else
                    {
                        port = "done";
                        var reason = shouldContinue ? "maximum-iterations" : "predicate-false";
                        await Emit(ExecutionEventKinds.LoopStopped, Id, invocation, null,
                            JsonSerializer.Serialize(new { reason, iterations = received.LoopIteration, maximumIterations = loop.MaximumIterations }));
                    }
                }
                if (!internalTerminationHandoff)
                    await Emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null,
                        routeSelection is not null ? JsonSerializer.Serialize(new { routeId = agentRouteId, port }) :
                        node.Type == NodeTypes.Loop ? JsonSerializer.Serialize(new { port, iteration = received.LoopIteration, maximumIterations = node.Loop!.MaximumIterations }) :
                        node.Type == NodeTypes.WorkflowCall && childOutcome!.Failed ? "Nested workflow ended at a failure termination." :
                        node.Type == NodeTypes.WorkflowCall && childOutcome!.Terminated ? "Nested workflow terminated successfully." :
                        node.Type == NodeTypes.Terminate && node.Termination?.Outcome == WorkflowTerminationOutcomes.Failure ? "Workflow ended at a failure termination." :
                        node.Type == NodeTypes.Terminate || received.TerminationSignal ? "Workflow terminated without returning a value." : result);
                if (node.Type == NodeTypes.ParallelFork)
                {
                    foreach (var edge in revision.Definition.Connections.Values.Where(edge => edge.Source.NodeId == Id))
                    {
                        await context.SendMessageAsync(new GraphMessage(result, Id, edge.Source.PortId, DataType: "string"), edge.Target.NodeId, cancellationToken);
                    }
                    return new(result, Id, "parallel.dispatched", DataType: "string");
                }
                var outputType = node.Type == NodeTypes.Terminate ? "string" : node.Type == NodeTypes.Output ? received.DataType :
                    node.Type == NodeTypes.Agent && node.Agent?.RequireStructuredResult == true ? "json" :
                    WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) ? "string" : WorkflowPorts.Outputs(node).Single(output => output.Id == port).DataType;
                var failureSignal = node.Termination?.Outcome == WorkflowTerminationOutcomes.Failure ||
                    received.FailureSignal || childOutcome?.Failed == true;
                var terminated = node.Type == NodeTypes.Terminate || received.TerminationSignal || childOutcome?.Terminated == true;
                if (routeSelection is not null)
                    await Emit(ExecutionEventKinds.AgentRouteCommitted, Id, invocation, null, JsonSerializer.Serialize(new { routeId = agentRouteId, port }));
                return new(result, internalTerminationHandoff ? received.SourceNode : Id, terminated ? "terminated" : port, permit, outputType, nextLoopIteration,
                    terminated, failureSignal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await Emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null, "Node execution cancelled.");
                throw;
            }
            catch (JsonProjectionContractException exception)
            {
                await Emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, exception.Message);
                throw;
            }
            catch
            {
                if (routeSelection is not null)
                {
                    await Emit(ExecutionEventKinds.AgentRouteRejected, Id, invocation, null,
                        JsonSerializer.Serialize(new { reason = routeSelection.RejectionReason }));
                    if (routeSelection.HasProposed)
                        await Emit(ExecutionEventKinds.AgentRouteDiscarded, Id, invocation, null,
                            JsonSerializer.Serialize(new { reason = "The agent did not complete successfully." }));
                }
                await Emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, "Node execution failed.");
                throw;
            }
        }

        private Guid ParallelJoinInvocation()
        {
            lock (parallelJoinSync) return parallelJoinInvocationId ??= Guid.NewGuid();
        }

        private async Task<WorkflowExecutionResult> ExecuteWorkflowCall(string input, CancellationToken cancellationToken)
        {
            var call = node.WorkflowCall ?? throw new InvalidOperationException("The nested workflow revision is unavailable.");
            var child = WorkflowRevision.Rehydrate(call.RevisionId, call.Definition, "embedded-workflow-prefab", null,
                DateTimeOffset.UnixEpoch, call.ContentHash);
            var childScope = scopeId is null ? Id : $"{scopeId}/{Id}";
            async ValueTask RecordChild(ExecutionEvent childEvent)
            {
                var kind = childEvent.Kind switch
                {
                    ExecutionEventKinds.Started => ExecutionEventKinds.SubworkflowStarted,
                    ExecutionEventKinds.Completed => ExecutionEventKinds.SubworkflowCompleted,
                    ExecutionEventKinds.Terminated => ExecutionEventKinds.SubworkflowTerminated,
                    ExecutionEventKinds.Failed => ExecutionEventKinds.SubworkflowFailed,
                    ExecutionEventKinds.Cancelled => ExecutionEventKinds.SubworkflowCancelled,
                    _ => childEvent.Kind
                };
                var nestedScope = childEvent.ScopeId is null ? childScope : $"{childScope}/{childEvent.ScopeId}";
                await Emit(kind, childEvent.NodeId, childEvent.InvocationId, childEvent.ConnectionId, childEvent.Data, nestedScope);
            }

            var runner = new WorkflowRunner(tools, agents);
            var childWorkflowPath = workflowPath.Add(new WorkflowApprovalScope(Id, child.RevisionId, child.ContentHash));
            IReadOnlyDictionary<string, string>? childParameters = call.ParameterMappings is { Length: > 0 } mappings
                ? mappings.ToDictionary(mapping => mapping.ChildParameterId, mapping => parameters![mapping.ParentParameterId], StringComparer.Ordinal)
                : null;
            return gate is null
                ? await runner.ExecuteCoreAsync(runId, child, input, childParameters, RecordChild, null, cancellationToken, rootRevision, childWorkflowPath)
                : await runner.ExecuteCoreAsync(runId, child, input, childParameters, RecordChild, gate, cancellationToken, rootRevision, childWorkflowPath);
        }

        private (bool Started, bool Complete, string? Result, int ReceivedCount) CollectParallelJoinInput(string inputPort, string input)
        {
            lock (parallelJoinSync)
            {
                if (inputPort is not ("branch_1" or "branch_2") || !parallelJoinInputs.TryAdd(inputPort, input))
                    throw new InvalidOperationException("The parallel join received an unexpected or duplicate branch result.");
                var started = parallelJoinInputs.Count == 1;
                if (parallelJoinInputs.Count < ParallelGraphLimits.MaximumBranches)
                    return (started, false, null, parallelJoinInputs.Count);

                var result = JsonSerializer.Serialize(parallelJoinInputs);
                if (result.Length > ParallelGraphLimits.MaximumAggregateCharacters)
                    throw new InvalidOperationException($"Parallel branch results exceed {ParallelGraphLimits.MaximumAggregateCharacters} characters.");
                return (started, true, result, parallelJoinInputs.Count);
            }
        }

        private async Task<string> InvokeTool(string input, Guid invocation, CancellationToken cancellationToken)
        {
            if (tools is null) throw new InvalidOperationException("No tool broker is configured.");
            await using var scope = tools.OpenScope(new(runId, revision, Id, invocation,
                (kind, data) => Emit(kind, Id, invocation, null, data)));
            return await scope.InvokeAsync(node.Tool!.BindingId, input, cancellationToken);
        }

        private static string NormalizeJson(string input)
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) document.RootElement.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }

        private static string SelectJsonText(string input, string pointer)
        {
            if (input.Length > 16_384) throw new InvalidOperationException("JSON workflow data exceeds 16,384 characters.");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
            var selected = ResolvePointer(document.RootElement, WorkflowJsonPointer.Parse(pointer));

            if (selected.ValueKind == JsonValueKind.String) return selected.GetString()!;
            using var serialized = new MemoryStream();
            using (var writer = new Utf8JsonWriter(serialized)) selected.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(serialized.ToArray());
        }

        private static string ProjectJson(string input, JsonProjectionConfiguration projection)
        {
            if (input.Length > JsonProjectionLimits.MaximumPayloadCharacters)
                throw new InvalidOperationException($"JSON workflow data exceeds {JsonProjectionLimits.MaximumPayloadCharacters} characters.");
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
            using var serialized = new MemoryStream();
            using (var writer = new Utf8JsonWriter(serialized))
            {
                writer.WriteStartObject();
                foreach (var field in projection.Fields)
                {
                    writer.WritePropertyName(field.Name);
                    if (!TryResolvePointer(document.RootElement, WorkflowJsonPointer.Parse(field.Pointer), out var value))
                        throw new JsonProjectionContractException($"JSON pointer for projection field '{field.Name}' did not resolve.");
                    if (field.ValueSchema is { } schema)
                        WriteMappedValue(writer, value, schema, field.Name);
                    else
                    {
                        if (field.ValueType is { } expectedType && !MatchesJsonType(value, expectedType))
                            throw new JsonProjectionContractException($"JSON projection field '{field.Name}' expected {expectedType} but received {JsonTypeName(value)}.");
                        value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            var projected = System.Text.Encoding.UTF8.GetString(serialized.ToArray());
            if (projected.Length > JsonProjectionLimits.MaximumPayloadCharacters)
                throw new InvalidOperationException($"Projected JSON exceeds {JsonProjectionLimits.MaximumPayloadCharacters} characters.");
            return projected;
        }

        private static bool MatchesJsonType(JsonElement value, string expectedType) => expectedType switch
        {
            "any" => true,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && decimal.TryParse(value.GetRawText(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && decimal.Truncate(number) == number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };

        private static string JsonTypeName(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => "undefined"
        };

        private static void WriteMappedValue(Utf8JsonWriter writer, JsonElement value, JsonValueSchema schema, string path)
        {
            if (!MatchesJsonType(value, schema.Type))
                throw new JsonProjectionContractException($"Mapped JSON value '{path}' expected {schema.Type} but received {JsonTypeName(value)}.");

            switch (schema.Type)
            {
                case "object":
                    writer.WriteStartObject();
                    foreach (var property in schema.Properties!)
                    {
                        if (!TryResolvePointer(value, WorkflowJsonPointer.Parse(property.Pointer), out var nestedValue))
                        {
                            if (property.Optional) continue;
                            throw new JsonProjectionContractException($"Required mapped property '{path}.{property.Name}' was not found.");
                        }
                        writer.WritePropertyName(property.Name);
                        WriteMappedValue(writer, nestedValue, property.Schema, $"{path}.{property.Name}");
                    }
                    writer.WriteEndObject();
                    return;
                case "array":
                    var items = schema.Items!;
                    if (value.GetArrayLength() > JsonProjectionLimits.MaximumArrayItems)
                        throw new JsonProjectionContractException($"Mapped array '{path}' exceeds {JsonProjectionLimits.MaximumArrayItems} items.");
                    writer.WriteStartArray();
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                        WriteMappedValue(writer, item, items, $"{path}[{index++}]");
                    writer.WriteEndArray();
                    return;
                default:
                    value.WriteTo(writer);
                    return;
            }
        }

        private static JsonElement ResolvePointer(JsonElement root, IReadOnlyList<string> segments)
        {
            return TryResolvePointer(root, segments, out var selected)
                ? selected
                : throw new InvalidOperationException("The configured JSON pointer does not resolve to a value.");
        }

        private static bool TryResolvePointer(JsonElement root, IReadOnlyList<string> segments, out JsonElement value)
        {
            value = root;
            foreach (var segment in segments)
            {
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property))
                { value = property; continue; }
                if (value.ValueKind == JsonValueKind.Array && TryArrayIndex(segment, value.GetArrayLength(), out var index))
                { value = value[index]; continue; }
                value = default;
                return false;
            }
            return true;
        }

        private static bool TryArrayIndex(string segment, int length, out int index) =>
            int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) &&
            index < length && segment == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
