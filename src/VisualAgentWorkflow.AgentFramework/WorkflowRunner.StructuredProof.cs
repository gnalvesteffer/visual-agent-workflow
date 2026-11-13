using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed partial class WorkflowRunner
{
    /// <summary>
    /// Proof-only structured scheduler that reuses the production node executors
    /// while owning edge dispatch. It is not reachable through public execution.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteStructuredSchedulerProofAsync(Guid runId,
        WorkflowRevision revision, string input, IReadOnlyDictionary<string, string>? parameters,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default,
        string? activationScopeId = null, bool emitRunLifecycle = true, IWorkflowExecutionGate? gate = null,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted = null,
        bool stageForkTargetsBeforeDispatch = false,
        Func<int, string, ValueTask>? afterForkTargetStaged = null)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        WorkflowValidator.ThrowIfInvalid(revision.Definition);
        if (revision.Definition.ParallelCollaborationRegions is { Count: > 0 } ||
            revision.Definition.Nodes.Values.Any(node => node.Type == NodeTypes.Sequence))
            throw new NotSupportedException("The structured scheduler proof does not cover collaboration or Sequence nodes yet.");

        var definition = revision.Definition;
        var resolvedParameters = WorkflowParameters.Resolve(definition, parameters);
        var variableStore = new WorkflowVariableStore(definition);
        var dataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? new ExplicitGraphDataEvaluator(definition, input, resolvedParameters)
            : null;
        var executionEdges = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? definition.ExecutionConnections!.Values.ToArray()
            : definition.Connections.Values.ToArray();
        var recordGate = new SemaphoreSlim(1, 1);
        using var schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long sequence = 0;
        var executors = definition.Nodes.ToDictionary(entry => entry.Key,
            entry => (StringExecutor)new StringExecutor(entry.Key, entry.Value, runId, revision, resolvedParameters,
                tools, agents, dataEvaluator, variableStore, EmitNodeEvent, gate, null, revision, [],
                collaborationOptions, afterNodeStarted: afterNodeStarted));

        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? connectionId = null, string? data = null)
        {
            await recordGate.WaitAsync();
            try
            {
                await record(new ExecutionEvent(runId, revision.RevisionId, Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow, kind, nodeId, invocationId, connectionId, data)
                    { ScopeId = activationScopeId });
            }
            finally
            {
                recordGate.Release();
            }
        }

        ValueTask EmitNodeEvent(string kind, string? nodeId, Guid? invocationId,
            string? connectionId, string? data, string? _) =>
            Emit(kind, nodeId, invocationId, connectionId, data);

        try
        {
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Started);
            var inputType = WorkflowPorts.Outputs(definition.Nodes[definition.EntryNodeId]).Single().DataType;
            var output = await DispatchNode(definition.EntryNodeId,
                new GraphMessage(input, null, null, DataType: inputType), schedulerCancellation.Token);
            if (output is null) throw new InvalidOperationException("The structured proof produced no workflow output.");
            if (output.FailureSignal)
            {
                if (emitRunLifecycle)
                    await Emit(ExecutionEventKinds.Failed, output.SourceNode, data: "Workflow ended at a failure termination.");
                return new(null, true, true, HasOutput: false);
            }
            if (output.TerminationSignal)
            {
                if (emitRunLifecycle) await Emit(ExecutionEventKinds.Terminated, output.SourceNode);
                return new(null, true, HasOutput: false);
            }
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Completed, data: output.Text);
            return new(output.Text, false, HasOutput: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch
        {
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw;
        }
        finally
        {
            recordGate.Dispose();
        }

        async Task<GraphMessage?> DispatchNode(string nodeId, GraphMessage message, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var executor = executors[nodeId];
                var node = definition.Nodes[nodeId];
                var forkTasks = new List<Task<GraphMessage?>>();
                var stagedForkTargets = new List<(GraphMessage Message, string Target, CancellationToken Token)>();
                var result = await executor.HandleForStructuredSchedulerAsync(message, (outgoing, target, ct) =>
                {
                    if (node.Type == NodeTypes.ParallelFork && stageForkTargetsBeforeDispatch)
                    {
                        stagedForkTargets.Add((outgoing, target, ct));
                        return afterForkTargetStaged is null
                            ? ValueTask.CompletedTask
                            : afterForkTargetStaged(stagedForkTargets.Count, target);
                    }

                    forkTasks.Add(DispatchNode(target, outgoing, ct));
                    return ValueTask.CompletedTask;
                }, token);

                if (node.Type == NodeTypes.ParallelFork)
                {
                    foreach (var staged in stagedForkTargets)
                        forkTasks.Add(DispatchNode(staged.Target, staged.Message, staged.Token));
                    var branchResults = await Task.WhenAll(forkTasks);
                    var outputs = branchResults.Where(branch => branch is not null).ToArray();
                    return outputs.Length switch
                    {
                        0 => null,
                        1 => outputs[0],
                        _ => throw new InvalidOperationException("The structured proof produced multiple workflow outputs.")
                    };
                }

                if (node.Type == NodeTypes.Output) return result;
                if (result.SourcePort == "parallel.pending") return null;
                if (result.TerminationSignal)
                {
                    var outputNodeId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
                    return await DispatchNode(outputNodeId, result, token);
                }

                var next = executionEdges.SingleOrDefault(edge => edge.Source.NodeId == nodeId &&
                    edge.Source.PortId == result.SourcePort);
                return next is null ? null : await DispatchNode(next.Target.NodeId, result, token);
            }
            catch
            {
                if (!schedulerCancellation.IsCancellationRequested) schedulerCancellation.Cancel();
                throw;
            }
        }
    }

    /// <summary>
    /// Proof-only sequential activation coordinator. It keeps one run/event stream
    /// while creating an isolated production node/evaluator state set per item.
    /// This does not define or admit a public iterator graph contract.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteStructuredActivationSequenceProofAsync(Guid runId,
        WorkflowRevision bodyRevision, string jsonItems, string indexParameterId,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default, IWorkflowExecutionGate? gate = null,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted = null)
    {
        ArgumentNullException.ThrowIfNull(bodyRevision);
        ArgumentNullException.ThrowIfNull(jsonItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexParameterId);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));

        if (jsonItems.Length > 16_384)
            throw new ArgumentException("Activation proof input exceeds 16,384 characters.", nameof(jsonItems));
        var items = JsonSerializer.Deserialize<string[]>(jsonItems)
            ?? throw new ArgumentException("Activation proof input must be a JSON string array.", nameof(jsonItems));
        if (items.Any(item => item is null))
            throw new ArgumentException("Activation proof items must be non-null strings.", nameof(jsonItems));
        var sequence = 0L;
        var recordGate = new SemaphoreSlim(1, 1);
        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null)
        {
            await recordGate.WaitAsync(CancellationToken.None);
            try
            {
                await record(new ExecutionEvent(runId, bodyRevision.RevisionId,
                    Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, kind, nodeId, invocationId,
                    connectionId, data) { ScopeId = scopeId });
            }
            finally
            {
                recordGate.Release();
            }
        }

        try
        {
            await Emit(ExecutionEventKinds.Started);
            var results = new List<string>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scopeId = $"array-loop/{index}";
                var iterationId = Guid.NewGuid();
                await Emit(ExecutionEventKinds.LoopIterationStarted, "array-loop", iterationId, scopeId);
                var activationParameters = parameters?.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                activationParameters[indexParameterId] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var result = await ExecuteStructuredSchedulerProofAsync(runId, bodyRevision, items[index],
                    activationParameters, evt => evt.Kind is ExecutionEventKinds.Started or
                        ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                        ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated
                        ? ValueTask.CompletedTask
                        : Emit(evt.Kind, evt.NodeId, evt.InvocationId, scopeId, evt.Data, evt.ConnectionId),
                    cancellationToken, scopeId, emitRunLifecycle: false, gate: gate,
                    afterNodeStarted: afterNodeStarted);
                if (result.Failed || result.Output is null)
                {
                    await Emit(ExecutionEventKinds.Failed, "array-loop", iterationId, scopeId,
                        $"Activation failed at index {index}.");
                    await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
                    return new(null, true, true, HasOutput: false);
                }
                results.Add(result.Output);
                await Emit(ExecutionEventKinds.LoopIterationCompleted, "array-loop", iterationId, scopeId);
            }

            var output = string.Join(';', results);
            await Emit(ExecutionEventKinds.Completed, data: output);
            return new(output, false, HasOutput: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch
        {
            await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw;
        }
        finally
        {
            recordGate.Dispose();
        }
    }
}
