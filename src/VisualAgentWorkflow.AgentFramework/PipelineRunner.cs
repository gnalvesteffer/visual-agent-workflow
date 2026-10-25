using Microsoft.Agents.AI.Workflows;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Translates the supported string pipeline into actual Agent Framework executors and edges.</summary>
public sealed partial class PipelineRunner(INodeToolBroker? tools = null, IAgentNodeExecutor? agents = null)
{
    public Task<string> ExecuteAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken);

    public Task<string> ExecuteWithParametersAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(runId, revision, input, parameters, record, null, cancellationToken);

    public Task<string> ExecuteControlledAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken);
    }

    public Task<string> ExecuteControlledWithParametersAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        IWorkflowExecutionGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return ExecuteCoreAsync(runId, revision, input, parameters, record, gate, cancellationToken);
    }

    private async Task<string> ExecuteCoreAsync(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate? gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        WorkflowValidator.ThrowIfInvalid(revision.Definition);
        var resolvedParameters = WorkflowParameters.Resolve(revision.Definition, parameters);
        using var recordGate = new SemaphoreSlim(1, 1);
        long sequence = 0;
        async ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null, string? data = null)
        {
            await recordGate.WaitAsync();
            try
            {
                var evt = new ExecutionEvent(runId, revision.RevisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                    kind, node, invocation, connection, data);
                await record(evt);
            }
            finally { recordGate.Release(); }
        }

        var workflow = BuildWorkflow(runId, revision, resolvedParameters, Emit, gate);

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
            string? output = null;
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is WorkflowErrorEvent error)
                    throw new InvalidOperationException("Agent Framework execution failed.", error.Exception);
                if (evt is ExecutorFailedEvent failed)
                    throw new InvalidOperationException($"Executor {failed.ExecutorId} failed.", failed.Data);
                if (evt is WorkflowOutputEvent result && result.Data is GraphMessage message)
                    output = message.Text;
            }
            // The framework may finish its event stream normally when the execution token is cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null) throw new InvalidOperationException("The workflow produced no output.");
            if (gate is not null) await gate.WaitForBoundaryAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Emit(ExecutionEventKinds.Completed, data: output);
            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch
        {
            // Do not copy arbitrary exception messages or provider internals into captured payloads.
            await Emit(ExecutionEventKinds.Failed, data: "Pipeline execution failed.");
            throw;
        }
    }

    private Workflow BuildWorkflow(Guid runId, WorkflowRevision revision,
        IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate)
    {
        var definition = revision.Definition;
        var executors = definition.Nodes.ToDictionary(
            n => n.Key,
            n => new StringExecutor(n.Key, n.Value, runId, revision, parameters, tools, agents, emit, gate));
        var builder = new WorkflowBuilder(executors[definition.EntryNodeId]);
        foreach (var edge in definition.Connections.Values)
        {
            if (definition.Nodes[edge.Target.NodeId].Type == NodeTypes.ParallelJoin) continue;
            builder.AddEdge<GraphMessage>(executors[edge.Source.NodeId], executors[edge.Target.NodeId], message => message?.SourcePort == edge.Source.PortId);
        }
        var join = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.ParallelJoin);
        if (join.Value is not null)
        {
            var branchSources = definition.Connections.Values
                .Where(edge => edge.Target.NodeId == join.Key)
                .Select(edge => (ExecutorBinding)executors[edge.Source.NodeId]);
            builder.AddFanInBarrierEdge(branchSources, (ExecutorBinding)executors[join.Key]);
        }
        var outputId = definition.Nodes.Single(n => n.Value.Type == NodeTypes.Output).Key;
        builder.WithOutputFrom(executors[outputId]);
        return builder.Build();
    }

    private sealed record GraphMessage(string Text, string? SourceNode, string? SourcePort,
        WorkflowApprovalPermit? Approval = null, string DataType = "string");

    private sealed class StringExecutor(
        string id,
        NodeDefinition node,
        Guid runId,
        WorkflowRevision revision,
        IReadOnlyDictionary<string, string>? parameters,
        INodeToolBroker? tools,
        IAgentNodeExecutor? agents,
        Func<string, string?, Guid?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate)
        // BuildWorkflow creates a fresh executor set for each run; these instances may process branch messages concurrently within that run.
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        private sealed class JsonProjectionTypeMismatchException(string message) : InvalidOperationException(message) { }

        private readonly object parallelJoinSync = new();
        private readonly SemaphoreSlim parallelJoinExecution = new(1, 1);
        private readonly SortedDictionary<string, string> parallelJoinInputs = new(StringComparer.Ordinal);
        private Guid? parallelJoinInvocationId;

        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage received, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            if (gate is not null) await gate.WaitForBoundaryAsync(Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var message = received.Text;
            var invocation = node.Type == NodeTypes.ParallelJoin ? ParallelJoinInvocation() : received.Approval?.TargetInvocationId ?? Guid.NewGuid();
            // Receiving the downstream message is direct evidence of delivery, not inferred completion.
            string? incomingPort = null;
            if (received.SourceNode is not null)
            {
                var incoming = revision.Definition.Connections.Single(e => e.Value.Source.NodeId == received.SourceNode &&
                    e.Value.Source.PortId == received.SourcePort && e.Value.Target.NodeId == Id);
                var inputType = WorkflowPorts.Inputs(node).Single(port => port.Id == incoming.Value.Target.PortId).DataType;
                if (received.DataType != inputType) throw new InvalidOperationException("Connected workflow data type does not match the receiving node.");
                await emit(ExecutionEventKinds.Transfer, Id, invocation, incoming.Key, message);
                incomingPort = incoming.Value.Target.PortId;
            }
            if (node.Type == NodeTypes.ParallelJoin)
            {
                await parallelJoinExecution.WaitAsync(cancellationToken);
                try
                {
                    var (started, complete, result, receivedCount) = CollectParallelJoinInput(incomingPort!, message);
                    if (started) await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, "Waiting for both parallel branch results.");
                    await emit(ExecutionEventKinds.ParallelJoinProgress, Id, invocation, null,
                        complete ? "Both branch results received." : $"Received {receivedCount} of 2 branch results; waiting for the other branch.");
                    if (!complete) return new("", Id, "parallel.pending", DataType: "json");
                    await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, result);
                    return new(result!, Id, "out", DataType: "json");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null, "Parallel join cancelled.");
                    throw;
                }
                catch
                {
                    await emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, "Parallel join failed.");
                    throw;
                }
                finally { parallelJoinExecution.Release(); }
            }
            await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message);
            try
            {
                var result = node.Type switch
                {
                    NodeTypes.Input when node.InputDataType == "json" => NormalizeJson(message),
                    NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Approval => message,
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
                    NodeTypes.Agent => agents is null ? throw new InvalidOperationException("No agent executor is configured.") :
                        await agents.ExecuteAsync(new NodeInvocationContext(runId, revision, Id, invocation,
                            (kind, data) => emit(kind, Id, invocation, null, data))
                        {
                            Parameters = parameters,
                            RecordWithInvocation = (eventInvocationId, kind, data) => emit(kind, Id, eventInvocationId, null, data)
                        }, message, cancellationToken),
                    _ => throw new InvalidOperationException("Unsupported executor type.")
                };
                WorkflowApprovalPermit? permit = null;
                if (node.Type == NodeTypes.Approval)
                {
                    if (gate is not IWorkflowApprovalGate approvals) throw new InvalidOperationException("No approval coordinator is configured.");
                    var targetId = revision.Definition.Connections.Values.Single(e => e.Source.NodeId == Id).Target.NodeId;
                    var target = revision.Definition.Nodes[targetId];
                    var action = new WorkflowApprovalAction(runId, revision.RevisionId, revision.ContentHash, Id, invocation,
                        targetId, Guid.NewGuid(), target.Tool!, message, WorkflowApprovalAction.HashInput(message));
                    permit = await approvals.WaitForApprovalAsync(action, node.Approval!.Prompt, cancellationToken);
                    if (permit.TargetInvocationId != action.TargetInvocationId || permit.ActionHash != action.ComputeHash() || permit.RequestId == Guid.Empty)
                        throw new InvalidOperationException("The approval permit does not match the recorded action.");
                }
                var port = "out";
                if (node.Type == NodeTypes.Condition)
                {
                    port = node.Condition!.Evaluate(message) ? "true" : "false";
                    await emit(ExecutionEventKinds.BranchSelected, Id, invocation, null, JsonSerializer.Serialize(new { port }));
                }
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, result);
                if (node.Type == NodeTypes.ParallelFork)
                {
                    foreach (var edge in revision.Definition.Connections.Values.Where(edge => edge.Source.NodeId == Id))
                    {
                        await context.SendMessageAsync(new GraphMessage(result, Id, edge.Source.PortId, DataType: "string"), edge.Target.NodeId, cancellationToken);
                    }
                    return new(result, Id, "parallel.dispatched", DataType: "string");
                }
                var outputType = node.Type == NodeTypes.Output ? received.DataType :
                    WorkflowPorts.Outputs(node).Single(output => output.Id == port).DataType;
                return new(result, Id, port, permit, outputType);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null, "Node execution cancelled.");
                throw;
            }
            catch (JsonProjectionTypeMismatchException exception)
            {
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, exception.Message);
                throw;
            }
            catch
            {
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, "Node execution failed.");
                throw;
            }
        }

        private Guid ParallelJoinInvocation()
        {
            lock (parallelJoinSync) return parallelJoinInvocationId ??= Guid.NewGuid();
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
                (kind, data) => emit(kind, Id, invocation, null, data)));
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
                    var value = ResolvePointer(document.RootElement, WorkflowJsonPointer.Parse(field.Pointer));
                    if (field.ValueType is { } expectedType && !MatchesJsonType(value, expectedType))
                        throw new JsonProjectionTypeMismatchException($"JSON projection field '{field.Name}' expected {expectedType} but received {JsonTypeName(value)}.");
                    value.WriteTo(writer);
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
            JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.Null => "null", _ => "undefined"
        };

        private static JsonElement ResolvePointer(JsonElement root, IReadOnlyList<string> segments)
        {
            var selected = root;
            foreach (var segment in segments)
            {
                selected = selected.ValueKind switch
                {
                    JsonValueKind.Object when selected.TryGetProperty(segment, out var property) => property,
                    JsonValueKind.Array when TryArrayIndex(segment, selected.GetArrayLength(), out var index) => selected[index],
                    _ => throw new InvalidOperationException("The configured JSON pointer does not resolve to a value.")
                };
            }
            return selected;
        }

        private static bool TryArrayIndex(string segment, int length, out int index) =>
            int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) &&
            index < length && segment == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
