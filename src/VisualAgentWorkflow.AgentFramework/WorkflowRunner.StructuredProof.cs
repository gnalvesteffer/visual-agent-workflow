using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed partial class WorkflowRunner
{
    internal const string StructuredProofBreakNodeType = "vaw.proof.array-break";

    internal sealed record StructuredActivationBreakSignal(string SourceNodeId, Guid SourceFrameId,
        Guid TargetFrameId, string ScopeId);

    private sealed record StructuredSchedulerProofResult(WorkflowExecutionResult Execution,
        StructuredActivationBreakSignal? BreakSignal = null);

    private static WorkflowDefinition ValidationProjection(WorkflowDefinition definition)
    {
        var proofBreakNodes = definition.Nodes.Where(entry => entry.Value.Type == StructuredProofBreakNodeType)
            .Select(entry => entry.Key).ToArray();
        if (proofBreakNodes.Length == 0) return definition;

        var nodes = definition.Nodes.ToBuilder();
        foreach (var nodeId in proofBreakNodes)
        {
            // The private proof node has no public pin contract. Project its existing
            // outgoing shape to a validator-known node only; dispatch below still
            // intercepts the original type and returns the typed activation signal.
            var outgoingPorts = definition.ExecutionConnections?.Values
                .Where(edge => edge.Source.NodeId == nodeId).Select(edge => edge.Source.PortId).ToArray() ?? [];
            nodes[nodeId] = outgoingPorts.Contains("out", StringComparer.Ordinal)
                ? new(NodeTypes.Agent, "1.0.0",
                    Agent: new("vaw.proof.break", null, "Private proof projection", []))
                : outgoingPorts.Length > 0
                    ? new(NodeTypes.McpTool, "1.0.0",
                        new ToolBindingRequest("vaw.proof.break", new string('0', 64))) { McpArgumentsJson = "{}" }
                    : new(NodeTypes.Terminate, "1.0.0")
                        { Termination = new(WorkflowTerminationOutcomes.Success) };
        }
        return definition with { Nodes = nodes.ToImmutable() };
    }

    internal sealed record StructuredActivationRunBudget(
        int MaximumEvents,
        long MaximumBytes,
        int ReservedFailureEvents,
        long ReservedFailureBytes,
        long MaximumOutcomeBytes,
        int MaximumConcurrentOutcomeReservations = int.MaxValue,
        int MaximumLoopActivations = int.MaxValue,
        int MaximumModelDispatches = int.MaxValue,
        int MaximumToolDispatches = int.MaxValue);

    internal sealed class StructuredActivationBudgetExceededException()
        : InvalidOperationException("The activation run work or evidence budget was exhausted.");

    private sealed class StructuredActivationRunContext(Guid runId, Guid revisionId,
        Func<ExecutionEvent, ValueTask> record, StructuredActivationRunBudget? budget) : IDisposable
    {
        private readonly SemaphoreSlim recordGate = new(1, 1);
        private readonly StructuredActivationBudgetLedger? ledger = budget is null ? null : new(budget);
        private long sequence;
        private bool started;
        private bool terminal;

        public async ValueTask EmitAsync(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null)
        {
            await recordGate.WaitAsync(CancellationToken.None);
            try
            {
                var startsRun = nodeId is null && kind == ExecutionEventKinds.Started;
                var endsRun = nodeId is null && kind is (ExecutionEventKinds.Completed or
                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled);
                if (startsRun)
                {
                    if (started || terminal) throw new InvalidOperationException("The activation run has already started.");
                }
                else if (endsRun)
                {
                    if (!started || terminal) throw new InvalidOperationException("The activation run already has a terminal outcome or has not started.");
                }

                var nextSequence = sequence + 1;
                var evt = new ExecutionEvent(runId, revisionId, nextSequence, DateTimeOffset.UtcNow,
                    kind, nodeId, invocationId, connectionId, data) { ScopeId = scopeId };
                var transition = ledger?.Check(evt);
                await record(evt);
                if (transition is { } accepted) ledger!.Commit(evt, accepted);
                sequence = nextSequence;
                if (startsRun) started = true;
                if (endsRun) terminal = true;
            }
            finally
            {
                recordGate.Release();
            }
        }

        public void Dispose() => recordGate.Dispose();
    }

    private sealed class StructuredActivationBudgetLedger
    {
        public enum EventClass { Ordinary, Failure, OutcomeRequest, OutcomeResult }
        public sealed record Transition(EventClass Class, string? ReservationId = null);

        private static readonly HashSet<string> FailureKinds =
        [ExecutionEventKinds.AgentFailed, ExecutionEventKinds.NodeFailed, ExecutionEventKinds.NodeCancelled,
            ExecutionEventKinds.ToolFailed, ExecutionEventKinds.ToolUncertain, ExecutionEventKinds.Failed,
            ExecutionEventKinds.OutcomeUnknown, ExecutionEventKinds.Cancelled];
        private readonly int ordinaryEventLimit;
        private readonly int reservedFailureEvents;
        private readonly long maximumBytes;
        private readonly long reservedFailureBytes;
        private readonly long maximumOutcomeBytes;
        private readonly int maximumConcurrentOutcomeReservations;
        private readonly int maximumLoopActivations;
        private readonly int maximumModelDispatches;
        private readonly int maximumToolDispatches;
        private readonly Dictionary<string, long> outcomeReservations = new(StringComparer.Ordinal);
        private int ordinaryEvents;
        private int failureEvents;
        private int loopActivations;
        private int modelDispatches;
        private int toolDispatches;
        private long ordinaryBytes;
        private long pendingOutcomeBytes;
        private long failureBytes;

        public StructuredActivationBudgetLedger(StructuredActivationRunBudget budget)
        {
            ordinaryEventLimit = budget.MaximumEvents - budget.ReservedFailureEvents;
            maximumBytes = budget.MaximumBytes;
            reservedFailureEvents = budget.ReservedFailureEvents;
            reservedFailureBytes = budget.ReservedFailureBytes;
            maximumOutcomeBytes = budget.MaximumOutcomeBytes;
            maximumConcurrentOutcomeReservations = budget.MaximumConcurrentOutcomeReservations;
            maximumLoopActivations = budget.MaximumLoopActivations;
            maximumModelDispatches = budget.MaximumModelDispatches;
            maximumToolDispatches = budget.MaximumToolDispatches;
            if (ordinaryEventLimit < 0 || reservedFailureEvents < FailureKinds.Count ||
                maximumBytes < reservedFailureBytes || reservedFailureBytes < 0 || maximumOutcomeBytes < 0 ||
                maximumConcurrentOutcomeReservations < 1 || maximumLoopActivations < 1 ||
                maximumModelDispatches < 1 || maximumToolDispatches < 1)
                throw new ArgumentOutOfRangeException(nameof(budget));
        }

        public Transition Check(ExecutionEvent evt)
        {
            var bytes = EventBytes(evt);
            if (evt.Kind == ExecutionEventKinds.LoopIterationStarted && loopActivations >= maximumLoopActivations ||
                evt.Kind == ExecutionEventKinds.ModelRequested && modelDispatches >= maximumModelDispatches ||
                evt.Kind == ExecutionEventKinds.ToolAuthorized && toolDispatches >= maximumToolDispatches)
                throw new StructuredActivationBudgetExceededException();

            if (TryGetReservationRequest(evt, out var requestId))
            {
                if (outcomeReservations.Count >= maximumConcurrentOutcomeReservations ||
                    ordinaryEvents + outcomeReservations.Count + 2 > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes + bytes + maximumOutcomeBytes > maximumBytes - reservedFailureBytes)
                    throw new StructuredActivationBudgetExceededException();
                return new(EventClass.OutcomeRequest, requestId);
            }
            if (evt.Kind is ExecutionEventKinds.ModelRequested or ExecutionEventKinds.ToolAuthorized)
                throw new InvalidOperationException("A dispatch event must include its reservation identity.");

            if (TryGetReservationOutcome(evt, out var outcomeId) && outcomeReservations.TryGetValue(outcomeId, out var reservedBytes))
            {
                if (bytes > reservedBytes || ordinaryEvents + outcomeReservations.Count > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes - reservedBytes + bytes > maximumBytes - reservedFailureBytes)
                    throw new InvalidOperationException("The activation outcome exceeded its reserved evidence budget.");
                return new(EventClass.OutcomeResult, outcomeId);
            }
            if (evt.Kind is ExecutionEventKinds.ModelResponded or ExecutionEventKinds.ToolCompleted or ExecutionEventKinds.ToolUncertain)
                throw new InvalidOperationException("An outcome event has no matching dispatch reservation.");

            if (FailureKinds.Contains(evt.Kind))
            {
                if (failureEvents >= reservedFailureEvents || failureBytes + bytes > reservedFailureBytes)
                    throw new InvalidOperationException("The activation failure evidence exceeded its reserved budget.");
                return new(EventClass.Failure);
            }

            if (ordinaryEvents + outcomeReservations.Count + 1 > ordinaryEventLimit ||
                ordinaryBytes + pendingOutcomeBytes + bytes > maximumBytes - reservedFailureBytes)
                throw new StructuredActivationBudgetExceededException();
            return new(EventClass.Ordinary);
        }

        public void Commit(ExecutionEvent evt, Transition transition)
        {
            var bytes = EventBytes(evt);
            switch (transition.Class)
            {
                case EventClass.OutcomeRequest:
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    outcomeReservations.Add(transition.ReservationId!, maximumOutcomeBytes);
                    pendingOutcomeBytes += maximumOutcomeBytes;
                    break;
                case EventClass.OutcomeResult:
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingOutcomeBytes -= outcomeReservations[transition.ReservationId!];
                    outcomeReservations.Remove(transition.ReservationId!);
                    break;
                case EventClass.Failure:
                    failureEvents++;
                    failureBytes += bytes;
                    break;
                default:
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    break;
            }

            if (evt.Kind == ExecutionEventKinds.LoopIterationStarted) loopActivations++;
            if (evt.Kind == ExecutionEventKinds.ModelRequested) modelDispatches++;
            if (evt.Kind == ExecutionEventKinds.ToolAuthorized) toolDispatches++;
        }

        private static long EventBytes(ExecutionEvent evt) =>
            JsonSerializer.SerializeToUtf8Bytes(evt, JsonSerializerOptions.Web).Length;

        private static bool TryGetReservationRequest(ExecutionEvent evt, out string reservationId) =>
            TryGetId(evt, evt.Kind switch
            {
                ExecutionEventKinds.ModelRequested => "modelCallId",
                ExecutionEventKinds.ToolAuthorized => "toolCallId",
                _ => null
            }, out reservationId);

        private static bool TryGetReservationOutcome(ExecutionEvent evt, out string reservationId) =>
            TryGetId(evt, evt.Kind switch
            {
                ExecutionEventKinds.ModelResponded => "modelCallId",
                ExecutionEventKinds.ToolCompleted or ExecutionEventKinds.ToolFailed or ExecutionEventKinds.ToolUncertain => "toolCallId",
                _ => null
            }, out reservationId);

        private static bool TryGetId(ExecutionEvent evt, string? propertyName, out string id)
        {
            id = string.Empty;
            if (propertyName is null || evt.Data is null) return false;
            try
            {
                using var document = JsonDocument.Parse(evt.Data);
                if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                    return false;
                id = value.GetString()!;
                return !string.IsNullOrWhiteSpace(id);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private sealed class StructuredProofSession
    {
        private Func<string, string?, Guid?, string?, string?, string?, ValueTask>? recordNodeEvent;

        public StructuredProofSession(Action? executorSetCreated) => ExecutorSetCreated = executorSetCreated;

        public Dictionary<string, StringExecutor>? Executors { get; set; }
        public Action? ExecutorSetCreated { get; }

        public void Bind(Func<string, string?, Guid?, string?, string?, string?, ValueTask> callback) =>
            recordNodeEvent = callback;

        public ValueTask RecordNodeEventAsync(string kind, string? nodeId, Guid? invocationId,
            string? connectionId, string? data, string? scopeId) =>
            recordNodeEvent?.Invoke(kind, nodeId, invocationId, connectionId, data, scopeId) ?? ValueTask.CompletedTask;
    }

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
        var result = await ExecuteStructuredSchedulerProofCoreAsync(runId, revision, input, parameters, record,
            cancellationToken, activationScopeId, emitRunLifecycle, gate, afterNodeStarted,
            stageForkTargetsBeforeDispatch, afterForkTargetStaged, null, null, null, null);
        return result.Execution;
    }

    private async Task<StructuredSchedulerProofResult> ExecuteStructuredSchedulerProofCoreAsync(Guid runId,
        WorkflowRevision revision, string input, IReadOnlyDictionary<string, string>? parameters,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken,
        string? activationScopeId, bool emitRunLifecycle, IWorkflowExecutionGate? gate,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted,
        bool stageForkTargetsBeforeDispatch,
        Func<int, string, ValueTask>? afterForkTargetStaged,
        ExplicitGraphDataEvaluator? retainedDataEvaluator,
        StructuredActivationFrame? activationFrame,
        Func<Guid?, WorkflowVariableStore?>? activationVariableStore,
        StructuredProofSession? retainedSession)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        WorkflowValidator.ThrowIfInvalid(ValidationProjection(revision.Definition));
        var collaborationRegion = revision.Definition.ParallelCollaborationRegions?.Values.SingleOrDefault();
        if (revision.Definition.ParallelCollaborationRegions is { Count: > 1 })
            throw new NotSupportedException("The structured scheduler proof covers one collaboration region per activation.");
        var collaborationTimeout = ResolveCollaborationTimeout(collaborationRegion, collaborationOptions);

        var definition = revision.Definition;
        var resolvedParameters = WorkflowParameters.Resolve(definition, parameters);
        var variableStore = activationVariableStore?.Invoke(activationFrame?.Id) ?? new WorkflowVariableStore(definition);
        var dataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? retainedDataEvaluator ?? new ExplicitGraphDataEvaluator(definition, input, resolvedParameters)
            : null;
        var executionEdges = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? definition.ExecutionConnections!.Values.ToArray()
            : definition.Connections.Values.ToArray();
        var recordGate = new SemaphoreSlim(1, 1);
        long sequence = 0;
        var createsExecutorSet = retainedSession?.Executors is null;
        var executors = retainedSession?.Executors ?? definition.Nodes
            .Where(entry => entry.Value.Type != StructuredProofBreakNodeType).ToDictionary(entry => entry.Key,
            entry => (StringExecutor)new StringExecutor(entry.Key, entry.Value, runId, revision, resolvedParameters,
                tools, agents, dataEvaluator, variableStore,
                retainedSession is null ? EmitNodeEvent : retainedSession.RecordNodeEventAsync,
                gate, null, revision, [], collaborationOptions, afterNodeStarted: afterNodeStarted,
                activationVariableStore: activationVariableStore));
        if (retainedSession is not null)
        {
            if (createsExecutorSet)
            {
                retainedSession.Executors = executors;
                retainedSession.ExecutorSetCreated?.Invoke();
            }
            retainedSession.Bind(EmitNodeEvent);
        }

        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? connectionId = null, string? data = null, string? eventScope = null)
        {
            await recordGate.WaitAsync();
            try
            {
                await record(new ExecutionEvent(runId, revision.RevisionId, Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow, kind, nodeId, invocationId, connectionId, data)
                    { ScopeId = eventScope ?? activationScopeId });
            }
            finally
            {
                recordGate.Release();
            }
        }

        ValueTask EmitNodeEvent(string kind, string? nodeId, Guid? invocationId,
            string? connectionId, string? data, string? eventScope) =>
            Emit(kind, nodeId, invocationId, connectionId, data, eventScope);

        using var collaborationRuntime = collaborationRegion is null ? null :
            new ParallelCollaborationRuntime(runId, revision.RevisionId, collaborationRegion,
                collaborationTimeout!.Value, EmitNodeEvent);
        using var collaborationCancellation = collaborationRuntime is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, collaborationRuntime.TimeoutToken);
        using var schedulerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            collaborationCancellation?.Token ?? cancellationToken);
        var rendezvousArrivals = new Dictionary<string, Dictionary<string, GraphMessage>>(StringComparer.Ordinal);
        var rendezvousInvocations = new Dictionary<string, Dictionary<string, Guid>>(StringComparer.Ordinal);
        var rendezvousSync = new object();

        try
        {
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Started);
            var inputType = WorkflowPorts.Outputs(definition.Nodes[definition.EntryNodeId]).Single().DataType;
            var output = await DispatchNode(definition.EntryNodeId,
                new GraphMessage(input, null, null, DataType: inputType,
                    ActivationFrame: activationFrame, CollaborationRuntime: collaborationRuntime), schedulerCancellation.Token);
            if (output is null) throw new InvalidOperationException("The structured proof produced no workflow output.");
            if (output.ActivationBreakSignal is { } breakSignal)
                return new(new(null, false, HasOutput: false), breakSignal);
            if (output.FailureSignal)
            {
                if (emitRunLifecycle)
                    await Emit(ExecutionEventKinds.Failed, output.SourceNode, data: "Workflow ended at a failure termination.");
                return new(new(null, true, true, HasOutput: false));
            }
            if (output.TerminationSignal)
            {
                if (emitRunLifecycle) await Emit(ExecutionEventKinds.Terminated, output.SourceNode);
                return new(new(null, true, HasOutput: false));
            }
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Completed, data: output.Text);
            return new(new(output.Text, false, HasOutput: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (collaborationRuntime is not null)
                await collaborationRuntime.AbortAsync("Workflow execution was cancelled.");
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch (OperationCanceledException) when (collaborationRuntime?.IsTimedOut == true)
        {
            await collaborationRuntime.AbortAsync("The collaboration region timed out.");
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw new TimeoutException("The collaboration region timeout elapsed.");
        }
        catch
        {
            if (collaborationRuntime is not null)
                await collaborationRuntime.AbortAsync("A collaboration member or workflow node failed.");
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
                if (definition.Nodes[nodeId].Type == StructuredProofBreakNodeType)
                {
                    var frame = message.ActivationFrame ?? activationFrame
                        ?? throw new InvalidOperationException("The structured proof Break has no active iterator frame.");
                    var nearestFrame = frame.NearestLoop();
                    var signal = new StructuredActivationBreakSignal(nodeId, frame.Id, nearestFrame.Id, frame.ScopeId);
                    var invocationId = Guid.NewGuid();
                    await Emit(ExecutionEventKinds.NodeStarted, nodeId, invocationId, eventScope: frame.ScopeId);
                    if (afterNodeStarted is not null) await afterNodeStarted(nodeId, token);
                    await Emit(ExecutionEventKinds.NodeCompleted, nodeId, invocationId,
                        data: JsonSerializer.Serialize(signal, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        eventScope: frame.ScopeId);
                    return message with
                    {
                        SourceNode = nodeId,
                        SourcePort = "break",
                        ActivationBreakSignal = signal
                    };
                }
                if (definition.Nodes[nodeId].Type == NodeTypes.Sequence)
                    return await DispatchSequence(nodeId, message, token);
                if (definition.Nodes[nodeId].Type == NodeTypes.ParallelRendezvous)
                    return await DispatchCollaborationRendezvous(nodeId, message, token);

                var executor = executors[nodeId];
                var node = definition.Nodes[nodeId];
                var forkTasks = new List<Task<(GraphMessage? Message, Exception? Error)>>();
                var stagedForkTargets = new List<(GraphMessage Message, string Target, CancellationToken Token)>();
                using var forkCancellation = node.Type == NodeTypes.ParallelFork
                    ? CancellationTokenSource.CreateLinkedTokenSource(token)
                    : null;
                var result = await executor.HandleForStructuredSchedulerAsync(message, (outgoing, target, ct) =>
                {
                    if (node.Type == NodeTypes.ParallelFork && stageForkTargetsBeforeDispatch)
                    {
                        stagedForkTargets.Add((outgoing, target, ct));
                        return afterForkTargetStaged is null
                            ? ValueTask.CompletedTask
                            : afterForkTargetStaged(stagedForkTargets.Count, target);
                    }

                    if (forkCancellation is null)
                        forkTasks.Add(DispatchBranch(target, outgoing, ct, null));
                    else
                        forkTasks.Add(DispatchBranch(target, outgoing, ct, forkCancellation));
                    return ValueTask.CompletedTask;
                }, token);

                if (result.ActivationBreakSignal is not null) return result;

                if (node.Type == NodeTypes.ParallelFork)
                {
                    foreach (var staged in stagedForkTargets)
                        forkTasks.Add(DispatchBranch(staged.Target, staged.Message, staged.Token, forkCancellation));
                    var outcomes = await Task.WhenAll(forkTasks);
                    token.ThrowIfCancellationRequested();
                    var failure = outcomes.Select(outcome => outcome.Error)
                        .FirstOrDefault(error => error is not null and not OperationCanceledException);
                    if (failure is not null) throw failure;
                    var breakResult = outcomes.Select(outcome => outcome.Message)
                        .FirstOrDefault(branch => branch?.ActivationBreakSignal is not null);
                    if (breakResult is not null) return breakResult;
                    var outputs = outcomes.Select(outcome => outcome.Message).Where(branch => branch is not null).ToArray();
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

                async Task<(GraphMessage? Message, Exception? Error)> DispatchBranch(string target,
                    GraphMessage outgoing, CancellationToken branchToken, CancellationTokenSource? sharedCancellation)
                {
                    using var linkedCancellation = sharedCancellation is null
                        ? null
                        : CancellationTokenSource.CreateLinkedTokenSource(sharedCancellation.Token, branchToken);
                    try
                    {
                        var branch = await DispatchNode(target, outgoing,
                            linkedCancellation?.Token ?? branchToken);
                        if (branch?.ActivationBreakSignal is not null) sharedCancellation?.Cancel();
                        return (branch, null);
                    }
                    catch (Exception error)
                    {
                        if (sharedCancellation is null) throw;
                        if (error is not OperationCanceledException) sharedCancellation?.Cancel();
                        return (null, error);
                    }
                }
            }
            catch (Exception error)
            {
                if ((error is not OperationCanceledException || cancellationToken.IsCancellationRequested) &&
                    token == schedulerCancellation.Token)
                {
                    if (!schedulerCancellation.IsCancellationRequested) schedulerCancellation.Cancel();
                }
                throw;
            }
        }

        async Task<GraphMessage?> DispatchCollaborationRendezvous(string nodeId, GraphMessage message,
            CancellationToken token)
        {
            var runtime = message.CollaborationRuntime
                ?? throw new InvalidOperationException("The structured collaboration rendezvous has no activation runtime.");
            var region = runtime.Region;
            var phase = nodeId == region.SecondRendezvousNodeId
                ? ParallelCollaborationPhases.Phase2
                : ParallelCollaborationPhases.Phase1;
            var branchId = message.ParallelBranchId
                ?? throw new InvalidOperationException("A collaboration arrival has no stable branch ID.");
            var expectedArrival = executionEdges.SingleOrDefault(edge => edge.Target.NodeId == nodeId &&
                edge.Target.PortId == ParallelBranchLimits.PinId(branchId));
            if (expectedArrival is null || expectedArrival.Source.NodeId != message.SourceNode ||
                message.ParallelForkNodeId != region.ForkNodeId || message.ParallelActivationId != runtime.RegionInvocationId ||
                !region.Branches.Any(branch => branch.BranchId == branchId))
                throw new InvalidOperationException("The collaboration rendezvous received an unexpected branch message.");

            var invocationId = Guid.NewGuid();
            await Emit(ExecutionEventKinds.NodeStarted, nodeId, invocationId,
                data: "Waiting for every collaboration branch.");
            lock (rendezvousSync)
            {
                if (!rendezvousArrivals.TryGetValue(nodeId, out var arrivals))
                    rendezvousArrivals.Add(nodeId, arrivals = new(StringComparer.Ordinal));
                if (!arrivals.TryAdd(branchId, message))
                    throw new InvalidOperationException("A collaboration branch arrived more than once.");
                if (!rendezvousInvocations.TryGetValue(nodeId, out var invocations))
                    rendezvousInvocations.Add(nodeId, invocations = new(StringComparer.Ordinal));
                invocations.Add(branchId, invocationId);
            }

            try
            {
                var arrival = await runtime.ArriveAsync(phase, nodeId, branchId, invocationId, token);
                if (!arrival.Released) return null;

                GraphMessage[] arrivals;
                Guid[] invocations;
                lock (rendezvousSync)
                {
                    arrivals = region.Branches.Select(branch => rendezvousArrivals[nodeId][branch.BranchId]).ToArray();
                    invocations = region.Branches.Select(branch => rendezvousInvocations[nodeId][branch.BranchId]).ToArray();
                }
                var deliveries = new List<Task<GraphMessage?>>();
                for (var index = 0; index < region.Branches.Length; index++)
                {
                    var branch = region.Branches[index];
                    var outputPort = ParallelBranchLimits.PinId(branch.BranchId);
                    var destination = executionEdges.SingleOrDefault(edge => edge.Source.NodeId == nodeId &&
                        edge.Source.PortId == outputPort)
                        ?? throw new InvalidOperationException($"The collaboration branch '{branch.BranchId}' has no post-rendezvous route.");
                    var source = arrivals[index];
                    deliveries.Add(DispatchNode(destination.Target.NodeId, source with
                    {
                        SourceNode = nodeId,
                        SourcePort = outputPort,
                        ParallelBranchId = branch.BranchId,
                        CollaborationRuntime = runtime
                    }, token));
                    await Emit(ExecutionEventKinds.NodeCompleted, nodeId, invocations[index],
                        $"collaboration:{branch.BranchId}", runtime.IsPhased
                            ? $"The {phase} snapshot was released."
                            : "The shared snapshot was released.");
                }
                var results = await Task.WhenAll(deliveries);
                var outputs = results.Where(result => result is not null).ToArray();
                return outputs.Length switch
                {
                    0 => null,
                    1 => outputs[0],
                    _ => throw new InvalidOperationException("The collaboration rendezvous produced multiple workflow outputs.")
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await Emit(ExecutionEventKinds.NodeCancelled, nodeId, invocationId, data: "Collaboration rendezvous cancelled.");
                throw;
            }
            catch
            {
                await Emit(ExecutionEventKinds.NodeFailed, nodeId, invocationId, data: "Collaboration rendezvous failed.");
                throw;
            }
        }

        async Task<GraphMessage?> DispatchSequence(string nodeId, GraphMessage message, CancellationToken token)
        {
            var sequence = definition.Nodes[nodeId].Sequence
                ?? throw new InvalidOperationException("The Sequence node has no configured outputs.");
            var outputEdges = sequence.Outputs.Select(output =>
            {
                var portId = SequenceOutputLimits.PinId(output.Id);
                var edge = definition.ExecutionConnections!.Single(candidate => candidate.Value.Source.NodeId == nodeId &&
                    candidate.Value.Source.PortId == portId);
                return (OutputId: output.Id, PortId: portId, ConnectionId: edge.Key, Edge: edge.Value);
            }).ToArray();
            var connectionIds = outputEdges.ToDictionary(output => output.OutputId, output => output.ConnectionId,
                StringComparer.Ordinal);
            var sequenceInvocationId = Guid.NewGuid();
            var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, nodeId,
                outputEdges.Select(output => output.OutputId).ToArray(), connectionIds);

            async ValueTask FlushSequenceEvents()
            {
                foreach (var runtimeEvent in coordinator.DrainEvents())
                {
                    var evidence = new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId,
                        runtimeEvent.SequenceNodeId, runtimeEvent.OutputId, runtimeEvent.Ordinal,
                        runtimeEvent.ActivationId, runtimeEvent.ConnectionId);
                    await Emit(runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                        runtimeEvent.ConnectionId, SequenceEvidence.Serialize(evidence));
                }
            }

            using var outputsCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Emit(ExecutionEventKinds.NodeStarted, nodeId, sequenceInvocationId, data: message.Text);
            await FlushSequenceEvents();
            try
            {
                var outputTasks = outputEdges.Select(async output =>
                {
                    var activationId = Guid.NewGuid();
                    var activation = coordinator.DispatchNext(activationId);
                    await FlushSequenceEvents();
                    var lineage = new SequenceMessageLineage(sequenceInvocationId, nodeId, activation.OutputId,
                        activation.Ordinal, activation.ActivationId!.Value, output.ConnectionId,
                        message.ActivationFrame?.ScopeId);
                    try
                    {
                        var result = await DispatchNode(output.Edge.Target.NodeId,
                            new GraphMessage(message.Text, nodeId, output.PortId, DataType: message.DataType,
                                ParallelActivationId: message.ParallelActivationId,
                                ParallelBranchId: message.ParallelBranchId,
                                ParallelForkNodeId: message.ParallelForkNodeId,
                                SequenceLineage: lineage, ActivationFrame: message.ActivationFrame,
                                CollaborationRuntime: message.CollaborationRuntime), outputsCancellation.Token);
                        if (result?.ActivationBreakSignal is not null) outputsCancellation.Cancel();
                        if (result is null)
                            return (ActivationId: activationId, OutputId: output.OutputId, Result: (GraphMessage?)null,
                                Error: (Exception?)new InvalidOperationException($"Sequence output '{output.OutputId}' produced no result."));
                        return (ActivationId: activationId, OutputId: output.OutputId, Result: result, Error: (Exception?)null);
                    }
                    catch (Exception error)
                    {
                        if (error is not OperationCanceledException) outputsCancellation.Cancel();
                        return (ActivationId: activationId, OutputId: output.OutputId, Result: (GraphMessage?)null,
                            Error: error);
                    }
                }).ToArray();

                var outcomes = await Task.WhenAll(outputTasks);
                token.ThrowIfCancellationRequested();
                var failure = outcomes.Select(outcome => outcome.Error)
                    .FirstOrDefault(error => error is not null and not OperationCanceledException);
                if (failure is not null)
                {
                    foreach (var completed in outcomes.Where(outcome => outcome.Result is not null))
                        coordinator.Complete(completed.ActivationId, SequenceTerminalState.Completed, completed.Result!.Text);
                    var failed = outcomes.First(outcome => outcome.Error is not null and not OperationCanceledException);
                    coordinator.Complete(failed.ActivationId, SequenceTerminalState.Failed);
                    await FlushSequenceEvents();
                    throw failure;
                }

                var breakResult = outcomes.Select(outcome => outcome.Result)
                    .FirstOrDefault(result => result?.ActivationBreakSignal is not null);
                if (breakResult is not null)
                {
                    await Emit("sequence.aborted", nodeId, sequenceInvocationId,
                        data: JsonSerializer.Serialize(breakResult.ActivationBreakSignal,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return breakResult;
                }

                foreach (var completed in outcomes)
                    coordinator.Complete(completed.ActivationId, SequenceTerminalState.Completed, completed.Result!.Text);
                await FlushSequenceEvents();
                var deterministicResult = coordinator.DeterministicResult
                    ?? throw new InvalidOperationException("The Sequence produced no completed output value.");
                await Emit(ExecutionEventKinds.NodeCompleted, nodeId, sequenceInvocationId,
                    data: "All Sequence outputs completed.");
                var firstOutputResult = outcomes[0].Result!;
                return firstOutputResult with { Text = deterministicResult };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                coordinator.CancelPending();
                await FlushSequenceEvents();
                await Emit(ExecutionEventKinds.NodeCancelled, nodeId, sequenceInvocationId,
                    data: "Sequence execution cancelled.");
                throw;
            }
            catch
            {
                coordinator.CancelPending();
                await FlushSequenceEvents();
                await Emit(ExecutionEventKinds.NodeFailed, nodeId, sequenceInvocationId,
                    data: "Sequence execution failed.");
                throw;
            }
        }
    }

    /// <summary>
    /// Proof-only sequential activation coordinator. It keeps one run/event stream,
    /// evaluator, executor set, and frame-scoped variable stores across items.
    /// This does not define or admit a public iterator graph contract.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteStructuredActivationSequenceProofAsync(Guid runId,
        WorkflowRevision bodyRevision, string jsonItems, string indexParameterId,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default, IWorkflowExecutionGate? gate = null,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted = null,
        Action? executorSetCreated = null,
        StructuredActivationFrame? parentFrame = null,
        string activationLoopNodeId = "array-loop",
        string? itemParameterId = null,
        string arrayElementType = WorkflowDataTypes.String,
        StructuredActivationRunBudget? runBudget = null)
    {
        ArgumentNullException.ThrowIfNull(bodyRevision);
        ArgumentNullException.ThrowIfNull(jsonItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationLoopNodeId);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));

        if (jsonItems.Length > 16_384)
            throw new ArgumentException("Activation proof input exceeds 16,384 characters.", nameof(jsonItems));
        if (!WorkflowArrayLiteral.TryParseValue(arrayElementType, jsonItems, out var typedItems, out var arrayError))
            throw new ArgumentException($"Activation proof input is not a valid {WorkflowDataTypes.ArrayOf(arrayElementType)}: {arrayError}",
                nameof(jsonItems));
        var items = typedItems.EnumerateArray().Select(item => arrayElementType switch
        {
            WorkflowDataTypes.String => item.GetString()!,
            WorkflowDataTypes.Boolean => item.GetBoolean() ? "true" : "false",
            WorkflowDataTypes.Integer => item.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkflowDataTypes.Number => item.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Activation proof input requires a primitive array element type.", nameof(arrayElementType))
        }).ToArray();
        var baseParameters = WorkflowParameters.Resolve(bodyRevision.Definition, parameters);
        var retainedDataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(bodyRevision.Definition)
            ? new ExplicitGraphDataEvaluator(bodyRevision.Definition, items.FirstOrDefault() ?? string.Empty, baseParameters)
            : null;
        var variableStores = new ConcurrentDictionary<Guid, WorkflowVariableStore>();
        var session = new StructuredProofSession(executorSetCreated);
        var runContext = new StructuredActivationRunContext(runId, bodyRevision.RevisionId, record, runBudget);
        ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null) =>
            runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);

        try
        {
            await Emit(ExecutionEventKinds.Started);
            var results = new List<string>(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inheritedParameters = baseParameters ??
                    ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
                if (parentFrame is not null)
                    inheritedParameters = inheritedParameters.SetItems(parentFrame.Parameters);
                var unboundFrame = parentFrame?.Enter(activationLoopNodeId, index, items[index], inheritedParameters,
                    arrayElementType) ?? new StructuredActivationFrame(Guid.NewGuid(), null, activationLoopNodeId,
                    index, items[index], inheritedParameters) { ItemDataType = arrayElementType };
                var frameParameters = unboundFrame.BindIteratorValues(bodyRevision.Definition, itemParameterId,
                    indexParameterId);
                var frame = unboundFrame with { Parameters = frameParameters };
                var activationParameters = frameParameters;
                var iterationId = frame.Id;
                if (parentFrame is not null && frame.NearestLoop().Id != frame.Id)
                    throw new InvalidOperationException("A nested activation did not resolve its nearest loop frame.");
                var scopeId = frame.ScopeId;
                await Emit(ExecutionEventKinds.LoopIterationStarted, activationLoopNodeId, iterationId, scopeId);
                var variableStore = variableStores.GetOrAdd(iterationId, _ => new WorkflowVariableStore(bodyRevision.Definition));
                var dispatchResult = await ExecuteStructuredSchedulerProofCoreAsync(runId, bodyRevision, items[index],
                    activationParameters, evt => evt.Kind is ExecutionEventKinds.Started or
                        ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                        ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated
                        ? ValueTask.CompletedTask
                        : Emit(evt.Kind, evt.NodeId, evt.InvocationId, evt.ScopeId ?? scopeId, evt.Data, evt.ConnectionId),
                    cancellationToken, scopeId, emitRunLifecycle: false, gate: gate,
                    afterNodeStarted: afterNodeStarted, stageForkTargetsBeforeDispatch: false,
                    afterForkTargetStaged: null, retainedDataEvaluator: retainedDataEvaluator,
                    activationFrame: frame, activationVariableStore: id =>
                        id is { } frameId && variableStores.TryGetValue(frameId, out var currentStore) ? currentStore : null,
                    retainedSession: session);
                if (dispatchResult.BreakSignal is { } breakSignal)
                {
                    frame.ValidateNearestBreak(breakSignal);
                    await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, iterationId,
                        scopeId, JsonSerializer.Serialize(new { outcome = "break", breakSignal.SourceNodeId,
                            breakSignal.TargetFrameId }));
                    break;
                }

                var result = dispatchResult.Execution;
                if (result.Failed || result.Output is null)
                {
                    await Emit(ExecutionEventKinds.Failed, activationLoopNodeId, iterationId, scopeId,
                        $"Activation failed at index {index}.");
                    await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
                    return new(null, true, true, HasOutput: false);
                }
                results.Add(result.Output);
                await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, iterationId, scopeId);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
            runContext.Dispose();
        }
    }
}
