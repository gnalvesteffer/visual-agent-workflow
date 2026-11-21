using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed partial class WorkflowRunner
{
    internal const string StructuredProofBreakNodeType = "vaw.proof.array-break";

    internal sealed record StructuredActivationBreakRequest(string SourceNodeId, Guid SourceFrameId,
        string ScopeId);

    internal sealed record StructuredActivationBreakSignal(string SourceNodeId, Guid SourceFrameId,
        Guid TargetFrameId, string ScopeId);

    private sealed record ScopedConcurrentBodyProofSettlement(WorkflowBodyProofDisposition Disposition,
        string? Output = null);

    private readonly record struct IterationReservationKey(string NodeId, Guid InvocationId, string? ScopeId);

    private sealed record StructuredSchedulerProofResult(WorkflowExecutionResult Execution,
        StructuredActivationBreakRequest? BreakRequest = null);

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

    internal async Task<WorkflowExecutionResult> ExecuteSingleStreamSequenceBreakProofAsync(
        Guid runId, WorkflowRevision revision, string input, Func<ExecutionEvent, ValueTask> record,
        IReadOnlySet<string> proofBreakNodeIds, CancellationToken cancellationToken = default,
        int maximumConcurrentOutputs = 2,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted = null)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(proofBreakNodeIds);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (proofBreakNodeIds.Count == 0) throw new ArgumentException("At least one proof Break node is required.", nameof(proofBreakNodeIds));

        var plan = CreateSequenceProductionPlan(revision)
            ?? throw new ArgumentException("The private proof requires a Sequence node.", nameof(revision));
        var parameters = WorkflowParameters.Resolve(revision.Definition, null) ??
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        var frame = new StructuredActivationFrame(Guid.NewGuid(), null, "array-loop", 0, input, parameters);
        var connections = plan.Outputs.ToDictionary(output => output.OutputId,
            output => output.ConnectionId, StringComparer.Ordinal);
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), plan.SequenceNodeId,
            plan.Outputs.Select(output => output.OutputId).ToArray(), connections, scopeId: frame.ScopeId);
        var session = new SequenceProductionDispatchSession(coordinator, frame, proofBreakNodeIds,
            maximumConcurrentOutputs);
        using var runContext = new StructuredActivationRunContext(runId, revision.RevisionId, record, null,
            reserveIterationSettlements: true);
        ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null) =>
            runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);
        using var sequenceEventGate = new SemaphoreSlim(1, 1);
        async ValueTask FlushSequenceEvents()
        {
            await sequenceEventGate.WaitAsync();
            try
            {
                var batch = coordinator.BeginEventDelivery();
                foreach (var runtimeEvent in batch.Events)
                {
                    var data = runtimeEvent.BreakAcceptance?.Serialize() ?? SequenceEvidence.Serialize(
                        new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId,
                            runtimeEvent.SequenceNodeId, runtimeEvent.OutputId, runtimeEvent.Ordinal,
                            runtimeEvent.ActivationId, runtimeEvent.ConnectionId, Reason: runtimeEvent.Reason));
                    try
                    {
                        await Emit(runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                            runtimeEvent.ScopeId ?? frame.ScopeId, data, runtimeEvent.ConnectionId);
                        coordinator.AcceptDeliveredEvent(batch.DeliveryId, runtimeEvent);
                    }
                    catch
                    {
                        coordinator.MarkEventDeliveryUncertain(batch.DeliveryId);
                        throw;
                    }
                }
            }
            finally
            {
                sequenceEventGate.Release();
            }
        }

        await Emit(ExecutionEventKinds.Started);
        await Emit(ExecutionEventKinds.LoopIterationStarted, "array-loop", frame.Id, frame.ScopeId);
        var outcome = await ExecuteBodyForActivationProofAsync(runId, revision, input, frame.Parameters,
            evt => Emit(evt.Kind, evt.NodeId, evt.InvocationId, evt.ScopeId ?? frame.ScopeId,
                evt.Data, evt.ConnectionId), cancellationToken, activationFrame: frame,
            afterNodeStarted: afterNodeStarted,
            proofBreakNodeIds: proofBreakNodeIds, sequencePlan: plan, sequenceDispatchSession: session);
        await FlushSequenceEvents();

        if (outcome.Disposition == WorkflowBodyProofDisposition.BreakRequested && outcome.BreakRequest is { } request)
        {
            var signal = ConsumeProductionBodyBreakRequestProof(frame, request, proofBreakNodeIds);
            await Emit(ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id, frame.ScopeId,
                JsonSerializer.Serialize(new { outcome = "break", signal.SourceNodeId, signal.TargetFrameId },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await Emit(ExecutionEventKinds.Completed, data: coordinator.DeterministicResult ?? string.Empty);
            return new(coordinator.DeterministicResult ?? string.Empty, false, HasOutput: true);
        }

        if (outcome.Disposition == WorkflowBodyProofDisposition.Succeeded)
        {
            await Emit(ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id, frame.ScopeId,
                JsonSerializer.Serialize(new { outcome = "completed" }));
            await Emit(ExecutionEventKinds.Completed, data: outcome.Output);
            return new(outcome.Output, false, HasOutput: true);
        }

        if (outcome.Disposition is WorkflowBodyProofDisposition.Cancelled or WorkflowBodyProofDisposition.TimedOut)
        {
            coordinator.CancelPending();
            await FlushSequenceEvents();
            await Emit(ExecutionEventKinds.Cancelled);
            return new(null, false, Cancelled: true, HasOutput: false);
        }

        if (outcome.Disposition == WorkflowBodyProofDisposition.OutcomeUnknown)
        {
            coordinator.CancelPending();
            await FlushSequenceEvents();
            await Emit(ExecutionEventKinds.OutcomeUnknown,
                data: "Workflow execution ended with an unresolved external outcome.");
            return new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false);
        }

        coordinator.CancelPending();
        await FlushSequenceEvents();
        const string failure = "Sequence output failed.";
        await Emit(ExecutionEventKinds.NodeFailed, plan.SequenceNodeId, scopeId: frame.ScopeId, data: failure);
        await Emit(ExecutionEventKinds.LoopIterationCompleted, "array-loop", frame.Id, frame.ScopeId,
            JsonSerializer.Serialize(new { outcome = "failed" }));
        await Emit(ExecutionEventKinds.Failed, data: failure);
        return new(null, true, Failed: true, HasOutput: false);
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
        : InvalidOperationException("The run evidence budget or aggregate work limit was exhausted.");

    internal sealed class StructuredActivationRunContext(Guid runId, Guid revisionId,
        Func<ExecutionEvent, ValueTask> record, StructuredActivationRunBudget? budget,
        bool reserveIterationSettlements = false) : IDisposable
    {
        private readonly SemaphoreSlim recordGate = new(1, 1);
        private readonly ExecutionActivationLedger activationLedger = new();
        private readonly StructuredActivationBudgetLedger? ledger = budget is null ? null :
            new(budget, reserveIterationSettlements);
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
                    ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated or
                    ExecutionEventKinds.OutcomeUnknown);
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
                activationLedger.Validate(evt);
                var transition = ledger?.Check(evt);
                await record(evt);
                if (transition is { } accepted) ledger!.Commit(evt, accepted);
                activationLedger.Observe(evt);
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
        private const int MaximumIterationCompletionDataCharacters = 1_024;
        public enum EventClass { Ordinary, Failure, OutcomeRequest, OutcomeResult, IterationStart, IterationResult }
        public sealed record Transition(EventClass Class, string? ReservationId = null,
            IterationReservationKey? IterationKey = null);

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
        private readonly bool reserveIterationSettlements;
        private readonly Dictionary<string, long> outcomeReservations = new(StringComparer.Ordinal);
        private readonly Dictionary<IterationReservationKey, long> iterationReservations = [];
        private int ordinaryEvents;
        private int failureEvents;
        private int loopActivations;
        private int modelDispatches;
        private int toolDispatches;
        private long ordinaryBytes;
        private long pendingOutcomeBytes;
        private long pendingIterationBytes;
        private long failureBytes;

        public StructuredActivationBudgetLedger(StructuredActivationRunBudget budget,
            bool reserveIterationSettlements)
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
            this.reserveIterationSettlements = reserveIterationSettlements;
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

            if (reserveIterationSettlements && evt.Kind == ExecutionEventKinds.LoopIterationStarted)
            {
                if (!TryGetIterationKey(evt, out var key) || iterationReservations.ContainsKey(key))
                    throw new InvalidOperationException("An iteration start requires a unique node, invocation, and scope identity.");
                var completionBytes = IterationCompletionReservationBytes(evt);
                if (ordinaryEvents + outcomeReservations.Count + iterationReservations.Count + 2 > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes + pendingIterationBytes + bytes + completionBytes >
                    maximumBytes - reservedFailureBytes)
                    throw new StructuredActivationBudgetExceededException();
                return new(EventClass.IterationStart, IterationKey: key);
            }

            if (reserveIterationSettlements && evt.Kind == ExecutionEventKinds.LoopIterationCompleted)
            {
                if (!TryGetIterationKey(evt, out var key) ||
                    !iterationReservations.TryGetValue(key, out var iterationReservedBytes))
                    throw new InvalidOperationException("An iteration completion has no matching settlement reservation.");
                if (bytes > iterationReservedBytes)
                    throw new InvalidOperationException("The iteration completion exceeded its reserved evidence budget.");
                return new(EventClass.IterationResult, IterationKey: key);
            }

            if (TryGetReservationRequest(evt, out var requestId))
            {
                if (outcomeReservations.Count >= maximumConcurrentOutcomeReservations ||
                    ordinaryEvents + outcomeReservations.Count + iterationReservations.Count + 2 > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes + pendingIterationBytes + bytes + maximumOutcomeBytes >
                    maximumBytes - reservedFailureBytes)
                    throw new StructuredActivationBudgetExceededException();
                return new(EventClass.OutcomeRequest, requestId);
            }
            if (evt.Kind is ExecutionEventKinds.ModelRequested or ExecutionEventKinds.ToolAuthorized)
                throw new InvalidOperationException("A dispatch event must include its reservation identity.");

            if (TryGetReservationOutcome(evt, out var outcomeId) && outcomeReservations.TryGetValue(outcomeId, out var reservedBytes))
            {
                if (bytes > reservedBytes || ordinaryEvents + outcomeReservations.Count + iterationReservations.Count > ordinaryEventLimit ||
                    ordinaryBytes + pendingOutcomeBytes + pendingIterationBytes - reservedBytes + bytes >
                    maximumBytes - reservedFailureBytes)
                    throw new InvalidOperationException("The activation outcome exceeded its reserved evidence budget.");
                return new(EventClass.OutcomeResult, outcomeId);
            }
            if (evt.Kind is ExecutionEventKinds.ModelResponded or ExecutionEventKinds.ToolCompleted or ExecutionEventKinds.ToolUncertain)
                throw new InvalidOperationException("An outcome event has no matching dispatch reservation.");

            if (FailureKinds.Contains(evt.Kind))
            {
                if (failureEvents >= reservedFailureEvents || failureBytes + bytes > reservedFailureBytes)
                    throw new StructuredActivationBudgetExceededException();
                return new(EventClass.Failure);
            }

            if (ordinaryEvents + outcomeReservations.Count + iterationReservations.Count + 1 > ordinaryEventLimit ||
                ordinaryBytes + pendingOutcomeBytes + pendingIterationBytes + bytes > maximumBytes - reservedFailureBytes)
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
                case EventClass.IterationStart:
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    var iterationBytes = IterationCompletionReservationBytes(evt);
                    iterationReservations.Add(transition.IterationKey!.Value, iterationBytes);
                    pendingIterationBytes += iterationBytes;
                    break;
                case EventClass.IterationResult:
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingIterationBytes -= iterationReservations[transition.IterationKey!.Value];
                    iterationReservations.Remove(transition.IterationKey.Value);
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

        private static long IterationCompletionReservationBytes(ExecutionEvent start) => EventBytes(start with
        {
            Sequence = long.MaxValue,
            Kind = ExecutionEventKinds.LoopIterationCompleted,
            Data = new string('x', MaximumIterationCompletionDataCharacters)
        });

        private static bool TryGetIterationKey(ExecutionEvent evt, out IterationReservationKey key)
        {
            if (!string.IsNullOrWhiteSpace(evt.NodeId) && evt.InvocationId is { } invocationId)
            {
                key = new(evt.NodeId, invocationId, evt.ScopeId);
                return true;
            }
            key = default;
            return false;
        }

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
                return TryGetId(document.RootElement, propertyName, 0, out id);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetId(JsonElement payload, string propertyName, int envelopeDepth, out string id)
        {
            id = string.Empty;
            if (payload.ValueKind != JsonValueKind.Object) return false;
            if (payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                id = value.GetString()!;
                if (!string.IsNullOrWhiteSpace(id)) return true;
            }

            const int maximumEnvelopeDepth = 4;
            if (envelopeDepth >= maximumEnvelopeDepth ||
                !payload.TryGetProperty("eventData", out var nested) || nested.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nested.GetString()))
                return false;

            try
            {
                using var nestedDocument = JsonDocument.Parse(nested.GetString()!);
                return TryGetId(nestedDocument.RootElement, propertyName, envelopeDepth + 1, out id);
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
    /// Proof-only activation coordinator using the production Agent Framework body
    /// dispatcher. One run context owns iteration frames, event sequencing, root
    /// lifecycle, and shared dispatch/evidence reservations for all body runs.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteProductionBodyActivationSequenceProofAsync(Guid runId,
        WorkflowRevision bodyRevision, string jsonItems, string indexParameterId,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default, StructuredActivationFrame? parentFrame = null,
        string activationLoopNodeId = "array-loop", string? itemParameterId = null,
        string arrayElementType = WorkflowDataTypes.String, StructuredActivationRunBudget? runBudget = null,
        IReadOnlySet<string>? proofBreakNodeIds = null, IWorkflowExecutionGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(bodyRevision);
        ArgumentNullException.ThrowIfNull(jsonItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationLoopNodeId);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (jsonItems.Length > 16_384)
            throw new ArgumentException("Activation proof input exceeds 16,384 characters.", nameof(jsonItems));
        proofBreakNodeIds ??= bodyRevision.Definition.Nodes
            .Where(entry => entry.Value.Type == StructuredProofBreakNodeType).Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!WorkflowArrayLiteral.TryParseValue(arrayElementType, jsonItems, out var typedItems, out var arrayError))
            throw new ArgumentException($"Activation proof input is not a valid {WorkflowDataTypes.ArrayOf(arrayElementType)}: {arrayError}",
                nameof(jsonItems));

        var items = ParseProductionActivationItems(typedItems, arrayElementType);
        var baseParameters = WorkflowParameters.Resolve(bodyRevision.Definition, parameters);
        using var runContext = new StructuredActivationRunContext(runId, bodyRevision.RevisionId, record, runBudget);
        var rootStarted = false;
        var rootTerminalAppendAttempted = false;
        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null)
        {
            var isRootStart = nodeId is null && kind == ExecutionEventKinds.Started;
            var isRootTerminal = nodeId is null && kind is (ExecutionEventKinds.Completed or
                ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated or
                ExecutionEventKinds.OutcomeUnknown);
            if (isRootTerminal) rootTerminalAppendAttempted = true;
            await runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);
            if (isRootStart) rootStarted = true;
        }

        async Task<WorkflowExecutionResult> FailRunAsync(string? reason = null)
        {
            await Emit(ExecutionEventKinds.Failed, data: reason ?? "Workflow execution failed.");
            return new(null, true, true, HasOutput: false);
        }

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
                var frame = unboundFrame with
                {
                    Parameters = unboundFrame.BindIteratorValues(bodyRevision.Definition, itemParameterId,
                        indexParameterId)
                };
                if (parentFrame is not null && frame.NearestLoop().Id != frame.Id)
                    throw new InvalidOperationException("A nested activation did not resolve its nearest loop frame.");

                var scopeId = frame.ScopeId;
                await Emit(ExecutionEventKinds.LoopIterationStarted, activationLoopNodeId, frame.Id, scopeId);
                var bodyOutcome = await ExecuteBodyForActivationProofAsync(runId, bodyRevision, items[index],
                    frame.Parameters, evt => Emit(evt.Kind, evt.NodeId, evt.InvocationId,
                        evt.ScopeId ?? scopeId, evt.Data, evt.ConnectionId), cancellationToken,
                    gate: gate, activationFrame: frame, proofBreakNodeIds: proofBreakNodeIds);

                var breakLoop = false;
                switch (bodyOutcome.Disposition)
                {
                    case WorkflowBodyProofDisposition.Succeeded when bodyOutcome.Output is { } bodyOutput:
                        results.Add(bodyOutput);
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, frame.Id,
                            scopeId);
                        break;
                    case WorkflowBodyProofDisposition.BreakRequested when bodyOutcome.BreakRequest is { } breakRequest:
                        var breakSignal = ConsumeProductionBodyBreakRequestProof(frame, breakRequest,
                            proofBreakNodeIds);
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, frame.Id,
                            scopeId, JsonSerializer.Serialize(new { outcome = "break", breakSignal.SourceNodeId,
                                breakSignal.TargetFrameId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        breakLoop = true;
                        break;
                    case WorkflowBodyProofDisposition.Terminated:
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, frame.Id,
                            scopeId, JsonSerializer.Serialize(new { outcome = "terminated" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        await Emit(ExecutionEventKinds.Terminated, data: JsonSerializer.Serialize(
                            new { terminalNodeId = bodyOutcome.TerminalNodeId },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        return new(null, true, HasOutput: false);
                    case WorkflowBodyProofDisposition.Cancelled:
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, frame.Id,
                            scopeId, JsonSerializer.Serialize(new { outcome = "cancelled" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        await Emit(ExecutionEventKinds.Cancelled);
                        return new(null, false, Cancelled: true, HasOutput: false);
                    case WorkflowBodyProofDisposition.OutcomeUnknown:
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, frame.Id,
                            scopeId, JsonSerializer.Serialize(new { outcome = "outcome-unknown" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        await Emit(ExecutionEventKinds.OutcomeUnknown,
                            data: "Workflow execution ended with an unresolved external outcome.");
                        return new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false);
                    case WorkflowBodyProofDisposition.Failed:
                    case WorkflowBodyProofDisposition.BudgetRejected:
                    case WorkflowBodyProofDisposition.TimedOut:
                    default:
                        return await FailRunAsync($"Activation ended with {bodyOutcome.Disposition} at index {index}.");
                    case WorkflowBodyProofDisposition.EvidenceWriteFailed:
                        throw bodyOutcome.EvidenceWriteError ??
                            new InvalidOperationException("The activation proof could not persist body evidence.");
                }
                if (breakLoop) break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var output = string.Join(';', results);
            await Emit(ExecutionEventKinds.Completed, data: output);
            return new(output, false, HasOutput: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            return new(null, false, Cancelled: true, HasOutput: false);
        }
        catch (StructuredActivationBudgetExceededException)
        {
            return await FailRunAsync();
        }
        catch (Exception exception) when (rootStarted && !rootTerminalAppendAttempted &&
            !ContainsEvidenceWriteFailure(exception))
        {
            // A nonterminal append may fail before commit or after an
            // ambiguous acknowledgement. Try one terminal marker using only
            // the accepted sequence; never retry the rejected event, and
            // preserve the original exception if this attempt also fails.
            try
            {
                await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            }
            catch (Exception)
            {
                // The original recorder failure remains the actionable error.
            }

            throw;
        }
    }

    /// <summary>
    /// Proof-only concurrent item coordinator. It admits at most two production
    /// body invocations at once and shares one root event/budget ledger. This is
    /// an experiment for production dispatch, not an authorable iterator.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteProductionConcurrentBodyBreakSequenceProofAsync(
        Guid runId, WorkflowRevision bodyRevision, string jsonItems, string indexParameterId,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default, StructuredActivationFrame? parentFrame = null,
        string activationLoopNodeId = "array-loop", string? itemParameterId = null,
        string arrayElementType = WorkflowDataTypes.String, StructuredActivationRunBudget? runBudget = null,
        IReadOnlySet<string>? proofBreakNodeIds = null,
        Func<int, CancellationToken, ValueTask>? afterItemAdmitted = null,
        Action<int>? onBreakAdmissionStopped = null,
        IWorkflowExecutionGate? gate = null,
        string? nestedItemsJson = null,
        string? nestedIndexParameterId = null,
        string? nestedActivationLoopNodeId = null,
        StructuredActivationRunContext? sharedRunContext = null,
        int maximumConcurrentBodies = 2)
    {
        ArgumentNullException.ThrowIfNull(bodyRevision);
        ArgumentNullException.ThrowIfNull(jsonItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationLoopNodeId);
        ArgumentNullException.ThrowIfNull(record);
        if ((nestedItemsJson is null) != (nestedIndexParameterId is null) ||
            (nestedItemsJson is null) != (nestedActivationLoopNodeId is null))
            throw new ArgumentException("Nested proof items, index parameter, and loop node must be supplied together.");
        if (sharedRunContext is not null && runBudget is not null)
            throw new ArgumentException("A nested proof must use its root coordinator's shared budget.", nameof(runBudget));
        if (maximumConcurrentBodies is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentBodies));
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (jsonItems.Length > 16_384)
            throw new ArgumentException("Activation proof input exceeds 16,384 characters.", nameof(jsonItems));
        proofBreakNodeIds ??= bodyRevision.Definition.Nodes
            .Where(entry => entry.Value.Type == StructuredProofBreakNodeType).Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!WorkflowArrayLiteral.TryParseValue(arrayElementType, jsonItems, out var typedItems, out var arrayError))
            throw new ArgumentException($"Activation proof input is not a valid {WorkflowDataTypes.ArrayOf(arrayElementType)}: {arrayError}",
                nameof(jsonItems));

        var items = ParseProductionActivationItems(typedItems, arrayElementType);
        if (nestedItemsJson is { Length: > 16_384 })
            throw new ArgumentException("Nested activation proof input exceeds 16,384 characters.", nameof(nestedItemsJson));
        var ownsRunContext = sharedRunContext is null;
        var baseParameters = WorkflowParameters.Resolve(bodyRevision.Definition, parameters);
        var runContext = sharedRunContext ?? new StructuredActivationRunContext(runId, bodyRevision.RevisionId,
            record, runBudget, reserveIterationSettlements: true);
        var admissionGate = new object();
        var admissionStopped = false;
        var nextIndex = 0;
        var results = new SortedDictionary<int, string>();
        var failureReason = (string?)null;
        var outcomeUnknown = false;
        var terminated = false;
        var cancelled = false;
        var rootStarted = false;
        var rootTerminalAppendAttempted = false;
        using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new Dictionary<int, Task<(int Index, StructuredActivationFrame Frame,
            WorkflowBodyProofOutcome Outcome)>>();
        var admittedFrames = new Dictionary<int, StructuredActivationFrame>();

        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null)
        {
            if (ownsRunContext && nodeId is null && kind is (ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown))
                rootTerminalAppendAttempted = true;
            await runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);
            if (nodeId is null && kind == ExecutionEventKinds.Started) rootStarted = true;
        }

        string? IterationCompletionData(WorkflowBodyProofOutcome outcome, StructuredActivationFrame frame)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return outcome.Disposition switch
            {
                WorkflowBodyProofDisposition.Succeeded => null,
                WorkflowBodyProofDisposition.BreakRequested when outcome.BreakRequest is { } request =>
                    SerializeBreakCompletion(frame, request, options),
                WorkflowBodyProofDisposition.Terminated => JsonSerializer.Serialize(new { outcome = "terminated" }, options),
                WorkflowBodyProofDisposition.Cancelled => JsonSerializer.Serialize(new { outcome = "cancelled" }, options),
                WorkflowBodyProofDisposition.OutcomeUnknown =>
                    JsonSerializer.Serialize(new { outcome = "outcome-unknown" }, options),
                _ => JsonSerializer.Serialize(new { outcome = "failed" }, options)
            };
        }

        string SerializeBreakCompletion(StructuredActivationFrame frame,
            StructuredActivationBreakRequest request, JsonSerializerOptions options)
        {
            var signal = ConsumeProductionBodyBreakRequestProof(frame, request, proofBreakNodeIds);
            return JsonSerializer.Serialize(new
            {
                outcome = "break",
                signal.SourceNodeId,
                signal.TargetFrameId
            }, options);
        }

        void StopAdmission() { lock (admissionGate) admissionStopped = true; }

        async Task<(int Index, StructuredActivationFrame Frame, WorkflowBodyProofOutcome Outcome)> RunBodyAsync(
            int index, StructuredActivationFrame frame)
        {
            var scopeId = frame.ScopeId;
            if (nestedItemsJson is not null)
            {
                var settlement = await RunNestedCoordinatorAsync(frame, scopeId);
                return (index, frame, new(settlement.Disposition, settlement.Output));
            }

            var outcome = await ExecuteBodyForActivationProofAsync(runId, bodyRevision, items[index],
                frame.Parameters, evt => Emit(evt.Kind, evt.NodeId, evt.InvocationId,
                    evt.ScopeId ?? scopeId, evt.Data, evt.ConnectionId), bodyCancellation.Token,
                gate: gate, activationFrame: frame, proofBreakNodeIds: proofBreakNodeIds,
                onBreakRequested: request =>
                {
                    _ = ConsumeProductionBodyBreakRequestProof(frame, request, proofBreakNodeIds);
                    int? stoppedAt = null;
                    lock (admissionGate)
                    {
                        if (!admissionStopped)
                        {
                            admissionStopped = true;
                            stoppedAt = nextIndex;
                        }
                    }
                    if (stoppedAt is { } boundary) onBreakAdmissionStopped?.Invoke(boundary);
                });
            return (index, frame, outcome);
        }

        async Task<ScopedConcurrentBodyProofSettlement> RunNestedCoordinatorAsync(
            StructuredActivationFrame frame, string scopeId)
        {
            var nestedResult = await ExecuteProductionConcurrentBodyBreakSequenceProofAsync(runId,
                    bodyRevision, nestedItemsJson, nestedIndexParameterId!, frame.Parameters,
                    evt => runContext.EmitAsync(evt.Kind, evt.NodeId, evt.InvocationId,
                        evt.ScopeId ?? scopeId, evt.Data, evt.ConnectionId), bodyCancellation.Token,
                    parentFrame: frame, activationLoopNodeId: nestedActivationLoopNodeId!,
                    itemParameterId: itemParameterId, arrayElementType: arrayElementType,
                    proofBreakNodeIds: proofBreakNodeIds, gate: gate, sharedRunContext: runContext);
            var disposition = nestedResult switch
            {
                { Cancelled: true } => WorkflowBodyProofDisposition.Cancelled,
                { OutcomeUnknown: true } => WorkflowBodyProofDisposition.OutcomeUnknown,
                { Failed: true } => WorkflowBodyProofDisposition.Failed,
                { Terminated: true } => WorkflowBodyProofDisposition.Terminated,
                { HasOutput: true, Output: not null } => WorkflowBodyProofDisposition.Succeeded,
                _ => WorkflowBodyProofDisposition.Failed
            };
            return new(disposition, nestedResult.Output);
        }

        try
        {
            if (ownsRunContext) await Emit(ExecutionEventKinds.Started);
            while (nextIndex < items.Length || active.Count > 0)
            {
                while (active.Count < maximumConcurrentBodies)
                {
                    lock (admissionGate)
                        if (admissionStopped || nextIndex >= items.Length) break;

                    if (gate is not null)
                        await gate.WaitForBoundaryAsync(activationLoopNodeId, cancellationToken);

                    int index;
                    lock (admissionGate)
                    {
                        if (admissionStopped || nextIndex >= items.Length) break;
                        index = nextIndex++;
                    }

                    if (afterItemAdmitted is not null)
                        await afterItemAdmitted(index, cancellationToken);

                    var inheritedParameters = baseParameters ??
                        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
                    if (parentFrame is not null)
                        inheritedParameters = inheritedParameters.SetItems(parentFrame.Parameters);
                    var unboundFrame = parentFrame?.Enter(activationLoopNodeId, index, items[index], inheritedParameters,
                        arrayElementType) ?? new StructuredActivationFrame(Guid.NewGuid(), null,
                        activationLoopNodeId, index, items[index], inheritedParameters)
                        { ItemDataType = arrayElementType };
                    var frame = unboundFrame with
                    {
                        Parameters = unboundFrame.BindIteratorValues(bodyRevision.Definition, itemParameterId,
                            indexParameterId)
                    };
                    if (parentFrame is not null && frame.NearestLoop().Id != frame.Id)
                        throw new InvalidOperationException("A nested activation did not resolve its nearest loop frame.");

                    await Emit(ExecutionEventKinds.LoopIterationStarted, activationLoopNodeId, frame.Id,
                        frame.ScopeId);
                    admittedFrames.Add(index, frame);
                    active.Add(index, RunBodyAsync(index, frame));
                }

                if (active.Count == 0) break;
                var completed = await Task.WhenAny(active.Values);
                var completedIndex = active.First(pair => ReferenceEquals(pair.Value, completed)).Key;
                var itemResult = await completed;
                active.Remove(completedIndex);
                var (settledIndex, settledFrame, bodyOutcome) = itemResult;
                admittedFrames.Remove(settledIndex);
                switch (bodyOutcome.Disposition)
                {
                    case WorkflowBodyProofDisposition.Succeeded when bodyOutcome.Output is { } output:
                        results[settledIndex] = output;
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId);
                        break;
                    case WorkflowBodyProofDisposition.BreakRequested when bodyOutcome.BreakRequest is { } request:
                        var signal = ConsumeProductionBodyBreakRequestProof(settledFrame, request, proofBreakNodeIds);
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId, JsonSerializer.Serialize(new { outcome = "break",
                                signal.SourceNodeId, signal.TargetFrameId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                    case WorkflowBodyProofDisposition.Terminated:
                        terminated = true;
                        StopAdmission();
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId, JsonSerializer.Serialize(new { outcome = "terminated" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                    case WorkflowBodyProofDisposition.Cancelled:
                        cancelled = true;
                        StopAdmission();
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId, JsonSerializer.Serialize(new { outcome = "cancelled" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                    case WorkflowBodyProofDisposition.OutcomeUnknown:
                        outcomeUnknown = true;
                        StopAdmission();
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId, JsonSerializer.Serialize(new { outcome = "outcome-unknown" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                    case WorkflowBodyProofDisposition.EvidenceWriteFailed:
                        throw bodyOutcome.EvidenceWriteError ??
                            new InvalidOperationException("The activation proof could not persist body evidence.");
                    default:
                        failureReason ??= $"Activation ended with {bodyOutcome.Disposition} at index {settledIndex}.";
                        StopAdmission();
                        await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                            settledFrame.Id, settledFrame.ScopeId, JsonSerializer.Serialize(new { outcome = "failed" },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (gate is not null)
                await gate.WaitForBoundaryAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (cancelled)
            {
                if (ownsRunContext) await Emit(ExecutionEventKinds.Cancelled);
                return new(null, false, Cancelled: true, HasOutput: false);
            }
            if (failureReason is not null)
            {
                if (ownsRunContext) await Emit(ExecutionEventKinds.Failed, data: failureReason);
                return new(null, true, true, HasOutput: false);
            }
            if (outcomeUnknown)
            {
                if (ownsRunContext)
                    await Emit(ExecutionEventKinds.OutcomeUnknown,
                        data: "Workflow execution ended with an unresolved external outcome.");
                return new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false);
            }
            if (terminated)
            {
                if (ownsRunContext) await Emit(ExecutionEventKinds.Terminated);
                return new(null, true, HasOutput: false);
            }

            var outputText = string.Join(';', results.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            if (ownsRunContext) await Emit(ExecutionEventKinds.Completed, data: outputText);
            return new(outputText, false, HasOutput: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopAdmission();
            bodyCancellation.Cancel();
            try { await Task.WhenAll(active.Values); }
            catch (Exception) { }
            foreach (var (index, frame) in admittedFrames.OrderBy(pair => pair.Key).ToArray())
            {
                var task = active.GetValueOrDefault(index);
                var outcome = task?.IsCompletedSuccessfully == true
                    ? task.Result.Outcome
                    : new WorkflowBodyProofOutcome(WorkflowBodyProofDisposition.Cancelled);
                active.Remove(index);
                admittedFrames.Remove(index);
                await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                    frame.Id, frame.ScopeId, IterationCompletionData(outcome, frame));
            }
            if (ownsRunContext) await Emit(ExecutionEventKinds.Cancelled);
            return new(null, false, Cancelled: true, HasOutput: false);
        }
        catch (StructuredActivationBudgetExceededException)
        {
            StopAdmission();
            bodyCancellation.Cancel();
            try { await Task.WhenAll(active.Values); }
            catch (Exception) { }
            foreach (var (index, frame) in admittedFrames.OrderBy(pair => pair.Key).ToArray())
            {
                var task = active.GetValueOrDefault(index);
                var outcome = task?.IsCompletedSuccessfully == true
                    ? task.Result.Outcome
                    : new WorkflowBodyProofOutcome(WorkflowBodyProofDisposition.BudgetRejected);
                active.Remove(index);
                admittedFrames.Remove(index);
                await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                    frame.Id, frame.ScopeId, IterationCompletionData(outcome, frame));
            }
            if (ownsRunContext)
                await Emit(ExecutionEventKinds.Failed, data: "Workflow execution budget was exhausted.");
            return new(null, true, true, HasOutput: false);
        }
        catch (Exception exception) when (ContainsEvidenceWriteFailure(exception))
        {
            StopAdmission();
            try { await Task.WhenAll(active.Values); }
            catch (Exception) { }
            throw;
        }
        catch (Exception) when (rootStarted && !rootTerminalAppendAttempted)
        {
            StopAdmission();
            bodyCancellation.Cancel();
            try { await Task.WhenAll(active.Values); }
            catch (Exception) { }
            foreach (var (index, frame) in admittedFrames.OrderBy(pair => pair.Key).ToArray())
            {
                var task = active.GetValueOrDefault(index);
                var outcome = task?.IsCompletedSuccessfully == true
                    ? task.Result.Outcome
                    : new WorkflowBodyProofOutcome(WorkflowBodyProofDisposition.Failed);
                active.Remove(index);
                admittedFrames.Remove(index);
                try
                {
                    await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId,
                        frame.Id, frame.ScopeId, IterationCompletionData(outcome, frame));
                }
                catch (Exception) { }
            }
            try { await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed."); }
            catch (Exception) { }
            throw;
        }
        finally
        {
            if (ownsRunContext) runContext.Dispose();
        }
    }

    /// <summary>
    /// Proof-only nested coordinator. The Agent Framework still dispatches one
    /// body activation at a time; this VAW-owned boundary controls both loop
    /// levels and the single root event lifecycle.
    /// </summary>
    internal async Task<WorkflowExecutionResult> ExecuteProductionNestedBodyBreakSequenceProofAsync(Guid runId,
        WorkflowRevision bodyRevision, string outerItemsJson, string innerItemsJson,
        string outerIndexParameterId, string innerIndexParameterId,
        IReadOnlyDictionary<string, string>? parameters, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default, string outerLoopNodeId = "outer-loop",
        string innerLoopNodeId = "inner-loop", string? itemParameterId = "item",
        string arrayElementType = WorkflowDataTypes.String,
        StructuredActivationRunBudget? runBudget = null,
        IReadOnlySet<string>? proofBreakNodeIds = null,
        SequenceProductionPlan? sequencePlan = null,
        int maximumConcurrentSequenceOutputs = 1,
        bool allowNestedForksProof = false)
    {
        ArgumentNullException.ThrowIfNull(bodyRevision);
        ArgumentNullException.ThrowIfNull(outerItemsJson);
        ArgumentNullException.ThrowIfNull(innerItemsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(outerIndexParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(innerIndexParameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outerLoopNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(innerLoopNodeId);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (maximumConcurrentSequenceOutputs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentSequenceOutputs));
        if (outerItemsJson.Length > 16_384 || innerItemsJson.Length > 16_384)
            throw new ArgumentException("Nested activation proof input exceeds 16,384 characters.");

        proofBreakNodeIds ??= bodyRevision.Definition.Nodes
            .Where(entry => entry.Value.Type == StructuredProofBreakNodeType).Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var outerItems = ParseProductionActivationItems(outerItemsJson, arrayElementType);
        var innerItems = ParseProductionActivationItems(innerItemsJson, arrayElementType);
        var baseParameters = WorkflowParameters.Resolve(bodyRevision.Definition, parameters) ??
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        using var runContext = new StructuredActivationRunContext(runId, bodyRevision.RevisionId, record, runBudget);
        ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null) =>
            runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);

        try
        {
            await Emit(ExecutionEventKinds.Started);
            var outerResults = new List<string>(outerItems.Length);
            for (var outerIndex = 0; outerIndex < outerItems.Length; outerIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outerFrame = new StructuredActivationFrame(Guid.NewGuid(), null, outerLoopNodeId,
                    outerIndex, outerItems[outerIndex], baseParameters);
                outerFrame = outerFrame with
                {
                    Parameters = outerFrame.BindIteratorValues(bodyRevision.Definition, null,
                        outerIndexParameterId)
                };
                await Emit(ExecutionEventKinds.LoopIterationStarted, outerLoopNodeId, outerFrame.Id,
                    outerFrame.ScopeId);
                var innerResults = new List<string>(innerItems.Length);
                var breakInnerLoop = false;

                for (var innerIndex = 0; innerIndex < innerItems.Length; innerIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var innerFrame = outerFrame.Enter(innerLoopNodeId, innerIndex, innerItems[innerIndex],
                        outerFrame.Parameters, arrayElementType);
                    innerFrame = innerFrame with
                    {
                        Parameters = innerFrame.BindIteratorValues(bodyRevision.Definition, itemParameterId,
                            innerIndexParameterId)
                    };
                    if (innerFrame.NearestLoop().Id != innerFrame.Id)
                        throw new InvalidOperationException("A nested activation did not resolve its nearest loop frame.");

                    await Emit(ExecutionEventKinds.LoopIterationStarted, innerLoopNodeId, innerFrame.Id,
                        innerFrame.ScopeId);
                    SequenceProductionDispatchSession? sequenceDispatchSession = null;
                    if (sequencePlan is not null)
                    {
                        var connectionIds = sequencePlan.Outputs.ToDictionary(output => output.OutputId,
                            output => output.ConnectionId, StringComparer.Ordinal);
                        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), sequencePlan.SequenceNodeId,
                            sequencePlan.Outputs.Select(output => output.OutputId).ToArray(), connectionIds,
                            sequencePlan.AllowSingleOutput, scopeId: innerFrame.ScopeId);
                        sequenceDispatchSession = new SequenceProductionDispatchSession(coordinator, innerFrame,
                            proofBreakNodeIds, maximumConcurrentSequenceOutputs);
                    }
                    var bodyOutcome = await ExecuteBodyForActivationProofAsync(runId, bodyRevision,
                        innerFrame.Item, innerFrame.Parameters,
                        evt => Emit(evt.Kind, evt.NodeId, evt.InvocationId,
                            evt.ScopeId ?? innerFrame.ScopeId, evt.Data, evt.ConnectionId), cancellationToken,
                        activationFrame: innerFrame, proofBreakNodeIds: proofBreakNodeIds,
                        sequencePlan: sequencePlan, sequenceDispatchSession: sequenceDispatchSession,
                        allowNestedForksProof: allowNestedForksProof);

                    switch (bodyOutcome.Disposition)
                    {
                        case WorkflowBodyProofDisposition.Succeeded when bodyOutcome.Output is { } bodyOutput:
                            innerResults.Add(bodyOutput);
                            await Emit(ExecutionEventKinds.LoopIterationCompleted, innerLoopNodeId,
                                innerFrame.Id, innerFrame.ScopeId);
                            break;
                        case WorkflowBodyProofDisposition.BreakRequested when bodyOutcome.BreakRequest is { } request:
                            var signal = ConsumeProductionBodyBreakRequestProof(innerFrame, request,
                                proofBreakNodeIds);
                            await Emit(ExecutionEventKinds.LoopIterationCompleted, innerLoopNodeId,
                                innerFrame.Id, innerFrame.ScopeId,
                                JsonSerializer.Serialize(new { outcome = "break", signal.SourceNodeId,
                                    signal.TargetFrameId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            breakInnerLoop = true;
                            break;
                        case WorkflowBodyProofDisposition.Terminated:
                            await Emit(ExecutionEventKinds.LoopIterationCompleted, innerLoopNodeId,
                                innerFrame.Id, innerFrame.ScopeId,
                                JsonSerializer.Serialize(new { outcome = "terminated" },
                                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            await Emit(ExecutionEventKinds.Terminated, data: JsonSerializer.Serialize(
                                new { terminalNodeId = bodyOutcome.TerminalNodeId },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            return new(null, true, HasOutput: false);
                        case WorkflowBodyProofDisposition.Cancelled:
                            await Emit(ExecutionEventKinds.LoopIterationCompleted, innerLoopNodeId,
                                innerFrame.Id, innerFrame.ScopeId,
                                JsonSerializer.Serialize(new { outcome = "cancelled" },
                                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            await Emit(ExecutionEventKinds.Cancelled);
                            return new(null, false, Cancelled: true, HasOutput: false);
                        case WorkflowBodyProofDisposition.OutcomeUnknown:
                            await Emit(ExecutionEventKinds.LoopIterationCompleted, innerLoopNodeId,
                                innerFrame.Id, innerFrame.ScopeId,
                                JsonSerializer.Serialize(new { outcome = "outcome-unknown" },
                                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                            await Emit(ExecutionEventKinds.OutcomeUnknown,
                                data: "Nested activation ended with an unresolved external outcome.");
                            return new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false);
                        case WorkflowBodyProofDisposition.EvidenceWriteFailed:
                            throw bodyOutcome.EvidenceWriteError ??
                                new InvalidOperationException("The nested activation could not persist evidence.");
                        default:
                            await Emit(ExecutionEventKinds.NodeFailed, innerLoopNodeId, innerFrame.Id,
                                innerFrame.ScopeId, $"Nested activation ended with {bodyOutcome.Disposition}.");
                            await Emit(ExecutionEventKinds.Failed, data: "Nested workflow execution failed.");
                            return new(null, true, Failed: true, HasOutput: false);
                    }

                    if (breakInnerLoop) break;
                }

                var outerOutput = $"{outerFrame.Item}:{string.Join(';', innerResults)}";
                outerResults.Add(outerOutput);
                await Emit(ExecutionEventKinds.LoopIterationCompleted, outerLoopNodeId, outerFrame.Id,
                    outerFrame.ScopeId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var output = string.Join(';', outerResults);
            await Emit(ExecutionEventKinds.Completed, data: output);
            return new(output, false, HasOutput: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            return new(null, false, Cancelled: true, HasOutput: false);
        }
        catch (StructuredActivationBudgetExceededException)
        {
            await Emit(ExecutionEventKinds.Failed, data: "Nested activation proof budget exhausted.");
            return new(null, true, Failed: true, HasOutput: false);
        }
    }

    internal static StructuredActivationBreakSignal ConsumeProductionBodyBreakRequestProof(
        StructuredActivationFrame frame, StructuredActivationBreakRequest request,
        IReadOnlySet<string> admittedBreakNodeIds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admittedBreakNodeIds);
        if (!admittedBreakNodeIds.Contains(request.SourceNodeId))
            throw new InvalidOperationException("The Break request source is not an admitted Break node.");
        return frame.ConsumeNearestBreak(request);
    }

    private static string[] ParseProductionActivationItems(JsonElement items, string elementType) =>
        items.EnumerateArray().Select(item => elementType switch
        {
            WorkflowDataTypes.String => item.GetString()!,
            WorkflowDataTypes.Boolean => item.GetBoolean() ? "true" : "false",
            WorkflowDataTypes.Integer => item.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkflowDataTypes.Number => item.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            WorkflowDataTypes.Json => item.GetRawText(),
            _ => throw new ArgumentException("Activation proof input requires a supported array element type.", nameof(elementType))
        }).ToArray();

    private static string[] ParseProductionActivationItems(string json, string elementType)
    {
        if (!WorkflowArrayLiteral.TryParseValue(elementType, json, out var items, out var error))
            throw new ArgumentException($"Activation proof input is not a valid {WorkflowDataTypes.ArrayOf(elementType)}: {error}",
                nameof(json));
        return ParseProductionActivationItems(items, elementType);
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
        Func<int, string, ValueTask>? afterForkTargetStaged = null,
        IReadOnlyDictionary<string, int>? proofDuplicateForkDeliveries = null)
    {
        var result = await ExecuteStructuredSchedulerProofCoreAsync(runId, revision, input, parameters, record,
            cancellationToken, activationScopeId, emitRunLifecycle, gate, afterNodeStarted,
            stageForkTargetsBeforeDispatch, afterForkTargetStaged, null, null, null, null, null,
            proofDuplicateForkDeliveries: proofDuplicateForkDeliveries);
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
        StructuredProofSession? retainedSession,
        IReadOnlySet<string>? proofBreakNodeIds,
        StructuredActivationFrameOwner? activationFrameOwner = null,
        IReadOnlyDictionary<string, int>? proofDuplicateForkDeliveries = null,
        StructuredProofDispatchLedger? sharedProofDispatchLedger = null,
        StructuredProofDispatchLedger.Delivery? initialProofDelivery = null,
        int proofWorkflowCallDepth = 0)
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
        var dispatchLedger = sharedProofDispatchLedger ??
            (proofDuplicateForkDeliveries is null ? null : new StructuredProofDispatchLedger());
        var ledgerActivations = new ConcurrentDictionary<Guid, Guid>();
        var stagedJoinMessages = new ConcurrentDictionary<Guid, GraphMessage>();
        var resolvedParameters = WorkflowParameters.Resolve(definition, parameters);
        StructuredActivationGraphPlan? activationGraphPlan = null;
        if (definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph &&
            definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Loop))
        {
            if (!StructuredActivationGraphPlan.TryCreate(definition, out activationGraphPlan,
                    out var topologyIssues))
                throw new InvalidOperationException("The proof loop activation topology could not be compiled: " +
                    string.Join(" ", topologyIssues.Select(issue => issue.Message)));
        }
        var variableStore = activationVariableStore?.Invoke(activationFrame?.Id) ?? new WorkflowVariableStore(definition);
        activationFrameOwner ??= new StructuredActivationFrameOwner(activationGraphPlan, activationFrame,
            definition, retainedDataEvaluator?.EvidenceBudget ?? variableStore.ArrayEvidenceBudget);
        var dataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? retainedDataEvaluator ?? new ExplicitGraphDataEvaluator(definition, input, resolvedParameters,
                activationFrameOwner.EvidenceBudget)
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
                activationVariableStore: activationVariableStore,
                activationFrameOwner: activationFrameOwner));
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
        var rendezvousDeliveries = new Dictionary<string,
            Dictionary<string, StructuredProofDispatchLedger.Delivery>>(StringComparer.Ordinal);
        var rendezvousSync = new object();

        try
        {
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Started);
            var inputType = WorkflowPorts.Outputs(definition.Nodes[definition.EntryNodeId]).Single().DataType;
            var rootMessage = new GraphMessage(input, null, null, DataType: inputType,
                ActivationFrame: activationFrame, CollaborationRuntime: collaborationRuntime);
            var output = await DispatchNode(definition.EntryNodeId, rootMessage, schedulerCancellation.Token,
                initialProofDelivery ?? dispatchLedger?.CreateRoot());
            if (output is null) throw new InvalidOperationException("The structured proof produced no workflow output.");
            if (output.ActivationBreakRequest is { } breakRequest)
                return new(new(null, false, HasOutput: false), breakRequest);
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
            dispatchLedger?.FailAll(new OperationCanceledException("Structured proof dispatch was cancelled."));
            if (collaborationRuntime is not null)
                await collaborationRuntime.AbortAsync("Workflow execution was cancelled.");
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch (OperationCanceledException) when (collaborationRuntime?.IsTimedOut == true)
        {
            dispatchLedger?.FailAll(new TimeoutException("Structured proof collaboration timed out."));
            await collaborationRuntime.AbortAsync("The collaboration region timed out.");
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw new TimeoutException("The collaboration region timeout elapsed.");
        }
        catch (Exception exception) when (IsOutcomeUnknownException(exception))
        {
            dispatchLedger?.FailAll(exception);
            if (collaborationRuntime is not null)
                await collaborationRuntime.AbortAsync("A workflow node ended with an unresolved external outcome.");
            if (emitRunLifecycle)
                await Emit(ExecutionEventKinds.OutcomeUnknown,
                    data: "Workflow execution ended with an unresolved external outcome.");
            return new(new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false));
        }
        catch
        {
            dispatchLedger?.FailAll(new InvalidOperationException("Structured proof dispatch failed."));
            if (collaborationRuntime is not null)
                await collaborationRuntime.AbortAsync("A collaboration member or workflow node failed.");
            if (emitRunLifecycle) await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw;
        }
        finally
        {
            recordGate.Dispose();
        }

        async Task<GraphMessage?> DispatchNode(string nodeId, GraphMessage message, CancellationToken token,
            StructuredProofDispatchLedger.Delivery? proofDelivery = null)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (definition.Nodes[nodeId].Type == StructuredProofBreakNodeType ||
                    proofBreakNodeIds?.Contains(nodeId) == true)
                {
                    var frame = message.ActivationFrame ?? activationFrame
                        ?? throw new InvalidOperationException("The structured proof Break has no active iterator frame.");
                    var request = new StructuredActivationBreakRequest(nodeId, frame.Id, frame.ScopeId);
                    if (!frame.BreakScope.TryRequest(request))
                        request = frame.BreakScope.Request ?? request;
                    var invocationId = Guid.NewGuid();
                    await Emit(ExecutionEventKinds.NodeStarted, nodeId, invocationId, eventScope: frame.ScopeId);
                    if (afterNodeStarted is not null) await afterNodeStarted(nodeId, token);
                    await Emit(ExecutionEventKinds.NodeCompleted, nodeId, invocationId,
                        data: JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        eventScope: frame.ScopeId);
                    return message with
                    {
                        SourceNode = nodeId,
                        SourcePort = "break",
                        ActivationBreakRequest = request
                    };
                }
                if (definition.Nodes[nodeId].Type == NodeTypes.Sequence)
                    return await DispatchSequence(nodeId, message, token, proofDelivery);
                if (definition.Nodes[nodeId].Type == NodeTypes.ParallelRendezvous)
                    return await DispatchCollaborationRendezvous(nodeId, message, token, proofDelivery);

                var executor = executors[nodeId];
                var node = definition.Nodes[nodeId];

                if (dispatchLedger is not null && node.Type == NodeTypes.ParallelJoin)
                {
                    var delivery = proofDelivery
                        ?? throw new InvalidOperationException("Proof Join dispatch has no admitted delivery identity.");
                    var activationId = message.ParallelActivationId is { } frameworkActivation &&
                        ledgerActivations.TryGetValue(frameworkActivation, out var ledgerActivation)
                            ? ledgerActivation
                            : throw new InvalidOperationException("Proof Join activation has no ledger identity.");
                    var branchId = message.ParallelBranchId
                        ?? throw new InvalidOperationException("Proof Join delivery has no branch identity.");
                    stagedJoinMessages[delivery.Id] = message;
                    var expectedBranches = node.ParallelJoin?.BranchIds
                        ?? throw new InvalidOperationException("Proof Join has no configured branch contract.");
                    var released = await dispatchLedger.StageJoinArrival(delivery, activationId, branchId,
                        expectedBranches).WaitAsync(token);
                    if (!dispatchLedger.TryClaimJoinBatch(activationId)) return null;

                    var releasedMessages = released.Select(arrival => stagedJoinMessages.TryGetValue(arrival.DeliveryId,
                            out var staged) ? (Arrival: arrival, Message: staged)
                        : throw new InvalidOperationException("A released Join delivery has no staged message.")).ToArray();
                    foreach (var arrival in released) stagedJoinMessages.TryRemove(arrival.DeliveryId, out _);
                    var duplicateGroup = releasedMessages.GroupBy(item => item.Arrival.BranchId,
                            StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
                    var messagesToDeliver = duplicateGroup is null
                        ? releasedMessages
                        : duplicateGroup.Take(2).ToArray();
                    GraphMessage? joinedResult = null;
                    foreach (var item in messagesToDeliver)
                    {
                        var branchResult = await executor.HandleForStructuredSchedulerAsync(item.Message,
                            (_, _, _) => throw new InvalidOperationException("A Join cannot dispatch branch work."), token);
                        if (branchResult.SourcePort != "parallel.pending") joinedResult = branchResult;
                    }
                    if (duplicateGroup is not null)
                        throw new InvalidOperationException("The structured proof admitted a duplicate Join arrival.");
                    if (joinedResult is null)
                        throw new InvalidOperationException("Quiescent unique Join arrivals produced no result.");

                    ledgerActivations.TryRemove(message.ParallelActivationId!.Value, out _);
                    var continuation = dispatchLedger.CollapseJoin(activationId);
                    return await DispatchScheduledResult(nodeId, joinedResult, token, continuation);
                }

                var forkTasks = new List<Task<(GraphMessage? Message, Exception? Error)>>();
                var stagedForkTargets = new List<(GraphMessage Message, string Target, CancellationToken Token)>();
                using var forkCancellation = node.Type == NodeTypes.ParallelFork
                    ? CancellationTokenSource.CreateLinkedTokenSource(token)
                    : null;
                var result = await executor.HandleForStructuredSchedulerAsync(message, (outgoing, target, ct) =>
                {
                    if (node.Type == NodeTypes.ParallelFork && (stageForkTargetsBeforeDispatch ||
                        dispatchLedger is not null || collaborationRuntime?.Region.ForkNodeId == nodeId))
                    {
                        stagedForkTargets.Add((outgoing, target, ct));
                        return afterForkTargetStaged is null
                            ? ValueTask.CompletedTask
                            : afterForkTargetStaged(stagedForkTargets.Count, target);
                    }

                    if (forkCancellation is null)
                        forkTasks.Add(DispatchBranch(target, outgoing, ct, null, null));
                    else
                        forkTasks.Add(DispatchBranch(target, outgoing, ct, forkCancellation, null));
                    return ValueTask.CompletedTask;
                }, token, dispatchLedger is not null &&
                    node.Type is (NodeTypes.WorkflowCall or NodeTypes.WorkflowInterfaceCall)
                    ? ExecuteProofWorkflowCall
                    : null);

                if (result.ActivationBreakRequest is not null) return result;

                if (node.Type == NodeTypes.ParallelFork)
                {
                    if (dispatchLedger is not null)
                    {
                        var current = proofDelivery
                            ?? throw new InvalidOperationException("Proof Fork dispatch has no admitted delivery identity.");
                        var stagedBranches = stagedForkTargets.Select(target => target.Message.ParallelBranchId
                            ?? throw new InvalidOperationException("Proof Fork output has no branch identity.")).ToArray();
                        var (activationId, branchDeliveries) = dispatchLedger.Fork(current, stagedBranches);
                        var frameworkActivationId = stagedForkTargets.Select(target => target.Message.ParallelActivationId)
                            .Distinct().SingleOrDefault()
                            ?? throw new InvalidOperationException("Proof Fork outputs do not share an activation identity.");
                        if (!ledgerActivations.TryAdd(frameworkActivationId, activationId))
                            throw new InvalidOperationException("Proof Fork activation identity was dispatched more than once.");

                        for (var index = 0; index < stagedForkTargets.Count; index++)
                        {
                            var staged = stagedForkTargets[index];
                            var branchId = stagedBranches[index];
                            var requestedDuplicates = proofDuplicateForkDeliveries?.GetValueOrDefault($"{nodeId}:{branchId}") ?? 0;
                            if (requestedDuplicates is < 0 or > 16)
                                throw new InvalidOperationException("Proof duplicate dispatch count must be between 0 and 16.");
                            var deliveries = new List<StructuredProofDispatchLedger.Delivery>
                                { branchDeliveries[index] };
                            for (var duplicateIndex = 0; duplicateIndex < requestedDuplicates; duplicateIndex++)
                                deliveries.Add(dispatchLedger.AdmitAdditionalDelivery(branchDeliveries[index]));
                            foreach (var admitted in deliveries)
                                forkTasks.Add(DispatchBranch(staged.Target, staged.Message, staged.Token,
                                    forkCancellation, admitted));
                        }
                    }
                    else
                    {
                    foreach (var staged in stagedForkTargets)
                        forkTasks.Add(DispatchBranch(staged.Target, staged.Message, staged.Token, forkCancellation, null));
                    }
                    var outcomes = await Task.WhenAll(forkTasks);
                    token.ThrowIfCancellationRequested();
                    var failure = outcomes.Select(outcome => outcome.Error)
                        .FirstOrDefault(error => error is not null and not OperationCanceledException);
                    if (failure is not null) throw failure;
                    var breakResult = outcomes.Select(outcome => outcome.Message)
                        .FirstOrDefault(branch => branch?.ActivationBreakRequest is not null);
                    if (breakResult is not null) return breakResult;
                    var outputs = outcomes.Select(outcome => outcome.Message).Where(branch => branch is not null).ToArray();
                    return outputs.Length switch
                    {
                        0 => null,
                        1 => outputs[0],
                        _ => throw new InvalidOperationException("The structured proof produced multiple workflow outputs.")
                    };
                }

                if (node.Type == NodeTypes.Output)
                {
                    if (dispatchLedger is not null && proofDelivery is not null)
                        dispatchLedger.Transition(proofDelivery, []);
                    return result;
                }
                if (result.SourcePort == "parallel.pending") return null;
                return await DispatchScheduledResult(nodeId, result, token, proofDelivery);

                async Task<WorkflowExecutionResult> ExecuteProofWorkflowCall(WorkflowCallConfiguration call,
                    string childInput, IReadOnlyDictionary<string, string>? currentParameters,
                    string? currentScopeId, CancellationToken childToken)
                {
                    if (proofWorkflowCallDepth != 0)
                        throw new NotSupportedException("The structured scheduler proof currently bounds WorkflowCall nesting to one child level.");
                    var parentDelivery = proofDelivery
                        ?? throw new InvalidOperationException("Proof WorkflowCall dispatch has no admitted delivery identity.");
                    var childDelivery = dispatchLedger!.AdmitChildDelivery(parentDelivery);
                    var childRevision = WorkflowRevision.Rehydrate(call.RevisionId, call.Definition,
                        "embedded-workflow-prefab", null, DateTimeOffset.UnixEpoch, call.ContentHash);
                    var childScope = currentScopeId is null ? nodeId : $"{currentScopeId}/{nodeId}";
                    IReadOnlyDictionary<string, string>? childParameters = call.ParameterMappings is { Length: > 0 } mappings
                        ? mappings.ToDictionary(mapping => mapping.ChildParameterId,
                            mapping => currentParameters![mapping.ParentParameterId], StringComparer.Ordinal)
                        : null;

                    async ValueTask RecordChild(ExecutionEvent childEvent)
                    {
                        var kind = childEvent.Kind switch
                        {
                            ExecutionEventKinds.Started => ExecutionEventKinds.SubworkflowStarted,
                            ExecutionEventKinds.Completed => ExecutionEventKinds.SubworkflowCompleted,
                            ExecutionEventKinds.Terminated => ExecutionEventKinds.SubworkflowTerminated,
                            ExecutionEventKinds.Failed => ExecutionEventKinds.SubworkflowFailed,
                            ExecutionEventKinds.Cancelled => ExecutionEventKinds.SubworkflowCancelled,
                            ExecutionEventKinds.OutcomeUnknown => ExecutionEventKinds.SubworkflowOutcomeUnknown,
                            _ => childEvent.Kind
                        };
                        var nestedScope = childEvent.ScopeId is null
                            ? childScope
                            : $"{childScope}/{childEvent.ScopeId}";
                        await Emit(kind, childEvent.NodeId, childEvent.InvocationId, childEvent.ConnectionId,
                            childEvent.Data, nestedScope);
                        if (kind is ExecutionEventKinds.SubworkflowStarted or ExecutionEventKinds.SubworkflowCompleted or
                            ExecutionEventKinds.SubworkflowTerminated or ExecutionEventKinds.SubworkflowFailed or
                            ExecutionEventKinds.SubworkflowCancelled or ExecutionEventKinds.SubworkflowOutcomeUnknown)
                            await Emit(ExecutionEventKinds.SubworkflowEvidence, nodeId, childEvent.InvocationId,
                                childEvent.ConnectionId,
                                new WorkflowCallLifecycleEvidence(kind, nodeId, childRevision.Definition.WorkflowId,
                                    childRevision.RevisionId, childRevision.ContentHash, nestedScope,
                                    childEvent.Data).Serialize(), nestedScope);
                    }

                    var childResult = await ExecuteStructuredSchedulerProofCoreAsync(runId, childRevision,
                        childInput, childParameters, RecordChild, childToken, activationScopeId: null, emitRunLifecycle: true,
                        gate, afterNodeStarted, stageForkTargetsBeforeDispatch, afterForkTargetStaged,
                        null, null, null, null, null, activationFrameOwner: null,
                        proofDuplicateForkDeliveries: proofDuplicateForkDeliveries ??
                            new Dictionary<string, int>(StringComparer.Ordinal),
                        sharedProofDispatchLedger: dispatchLedger, initialProofDelivery: childDelivery,
                        proofWorkflowCallDepth: proofWorkflowCallDepth + 1);
                    return childResult.Execution;
                }

                async Task<(GraphMessage? Message, Exception? Error)> DispatchBranch(string target,
                    GraphMessage outgoing, CancellationToken branchToken, CancellationTokenSource? sharedCancellation,
                    StructuredProofDispatchLedger.Delivery? admittedDelivery)
                {
                    using var linkedCancellation = sharedCancellation is null
                        ? null
                        : CancellationTokenSource.CreateLinkedTokenSource(sharedCancellation.Token, branchToken);
                    try
                    {
                        var branch = await DispatchNode(target, outgoing,
                            linkedCancellation?.Token ?? branchToken, admittedDelivery);
                        if (branch?.ActivationBreakRequest is not null) sharedCancellation?.Cancel();
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

        async ValueTask<GraphMessage?> DispatchScheduledResult(string sourceNode, GraphMessage nodeResult,
            CancellationToken dispatchToken, StructuredProofDispatchLedger.Delivery? currentDelivery)
        {
            var nextNodeId = nodeResult.TerminationSignal
                ? definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key
                : executionEdges.SingleOrDefault(edge => edge.Source.NodeId == sourceNode &&
                    edge.Source.PortId == nodeResult.SourcePort)?.Target.NodeId;
            if (nextNodeId is null)
            {
                if (dispatchLedger is not null && currentDelivery is not null)
                    dispatchLedger.Transition(currentDelivery, []);
                return null;
            }

            if (dispatchLedger is null)
                return await DispatchNode(nextNodeId, nodeResult, dispatchToken);
            if (currentDelivery is null)
                throw new InvalidOperationException("Proof dispatch transition has no current delivery identity.");
            var successor = dispatchLedger.Transition(currentDelivery,
                [new StructuredProofDispatchLedger.Successor(currentDelivery.Lineage)]).Single();
            return await DispatchNode(nextNodeId, nodeResult, dispatchToken, successor);
        }

        async Task<GraphMessage?> DispatchCollaborationRendezvous(string nodeId, GraphMessage message,
            CancellationToken token, StructuredProofDispatchLedger.Delivery? proofDelivery)
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
                if (dispatchLedger is not null)
                {
                    if (proofDelivery is null)
                        throw new InvalidOperationException("Proof collaboration arrival has no admitted delivery identity.");
                    if (!rendezvousDeliveries.TryGetValue(nodeId, out var deliveries))
                        rendezvousDeliveries.Add(nodeId, deliveries = new(StringComparer.Ordinal));
                    deliveries.Add(branchId, proofDelivery);
                }
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
                StructuredProofDispatchLedger.Delivery[]? deliveryTickets = null;
                lock (rendezvousSync)
                {
                    arrivals = region.Branches.Select(branch => rendezvousArrivals[nodeId][branch.BranchId]).ToArray();
                    invocations = region.Branches.Select(branch => rendezvousInvocations[nodeId][branch.BranchId]).ToArray();
                    if (dispatchLedger is not null)
                        deliveryTickets = region.Branches.Select(branch => rendezvousDeliveries[nodeId][branch.BranchId]).ToArray();
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
                    var outgoingDelivery = dispatchLedger is null
                        ? null
                        : dispatchLedger.Transition(deliveryTickets![index],
                            [new StructuredProofDispatchLedger.Successor(deliveryTickets[index].Lineage)]).Single();
                    deliveries.Add(DispatchNode(destination.Target.NodeId, source with
                    {
                        SourceNode = nodeId,
                        SourcePort = outputPort,
                        ParallelBranchId = branch.BranchId,
                        CollaborationRuntime = runtime
                    }, token, outgoingDelivery));
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

        async Task<GraphMessage?> DispatchSequence(string nodeId, GraphMessage message, CancellationToken token,
            StructuredProofDispatchLedger.Delivery? sequenceDelivery)
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
                outputEdges.Select(output => output.OutputId).ToArray(), connectionIds,
                scopeId: message.ActivationFrame?.ScopeId);
            var sequenceEventGate = new SemaphoreSlim(1, 1);

            async ValueTask FlushSequenceEvents()
            {
                await sequenceEventGate.WaitAsync();
                try
                {
                    var batch = coordinator.BeginEventDelivery();
                    foreach (var runtimeEvent in batch.Events)
                    {
                        var data = runtimeEvent.BreakAcceptance?.Serialize() ?? SequenceEvidence.Serialize(
                            new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId,
                                runtimeEvent.SequenceNodeId, runtimeEvent.OutputId, runtimeEvent.Ordinal,
                                runtimeEvent.ActivationId, runtimeEvent.ConnectionId, Reason: runtimeEvent.Reason));
                        try
                        {
                            await Emit(runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                                runtimeEvent.ConnectionId, data, runtimeEvent.ScopeId);
                            coordinator.AcceptDeliveredEvent(batch.DeliveryId, runtimeEvent);
                        }
                        catch
                        {
                            coordinator.MarkEventDeliveryUncertain(batch.DeliveryId);
                            throw;
                        }
                    }
                }
                finally
                {
                    sequenceEventGate.Release();
                }
            }

            using var outputsCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Emit(ExecutionEventKinds.NodeStarted, nodeId, sequenceInvocationId, data: message.Text);
            await FlushSequenceEvents();
            StructuredProofDispatchLedger.Delivery[]? outputDeliveries = null;
            StructuredProofDispatchLedger.Delivery? continuationDelivery = null;
            if (dispatchLedger is not null)
            {
                if (sequenceDelivery is null)
                    throw new InvalidOperationException("Proof Sequence dispatch has no admitted delivery identity.");
                var reserved = dispatchLedger.Transition(sequenceDelivery,
                    outputEdges.Select(_ => new StructuredProofDispatchLedger.Successor(sequenceDelivery.Lineage))
                        .Append(new(sequenceDelivery.Lineage))).ToArray();
                outputDeliveries = reserved[..outputEdges.Length];
                continuationDelivery = reserved[^1];
            }
            try
            {
                var outputTasks = outputEdges.Select(async (output, outputIndex) =>
                {
                    var outputDelivery = outputDeliveries?[outputIndex];
                    var activationId = Guid.NewGuid();
                    var activation = message.ActivationFrame is { } frame
                        ? frame.BreakScope.TryAdmit(() => coordinator.DispatchNext(activationId))
                        : coordinator.DispatchNext(activationId);
                    if (activation is null)
                    {
                        // A Break closes admission while its request event is being
                        // persisted. Do not publish skipped-output settlement until
                        // the Break request itself has been accepted.
                        if (dispatchLedger is not null && outputDelivery is not null)
                            dispatchLedger.Transition(outputDelivery, []);
                        return (ActivationId: activationId, OutputId: output.OutputId,
                            Result: (GraphMessage?)null, Error: (Exception?)null,
                            Lineage: (SequenceMessageLineage?)null);
                    }
                    await FlushSequenceEvents();
                    SequenceMessageLineage? lineage = new(sequenceInvocationId, nodeId, activation.OutputId,
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
                                CollaborationRuntime: message.CollaborationRuntime), outputsCancellation.Token,
                            outputDelivery);
                        if (result is null)
                            return (ActivationId: activationId, OutputId: output.OutputId, Result: (GraphMessage?)null,
                                Error: (Exception?)new InvalidOperationException($"Sequence output '{output.OutputId}' produced no result."),
                                Lineage: lineage);
                        if (result.ActivationBreakRequest is { } breakRequest)
                        {
                            var activeFrame = message.ActivationFrame
                                ?? throw new InvalidOperationException("The Sequence Break has no active frame.");
                            var target = activationFrameOwner.ConsumeBreak(activeFrame, breakRequest);
                            coordinator.AcceptBreak(lineage, breakRequest, target.TargetFrameId);
                            await FlushSequenceEvents();
                        }
                        return (ActivationId: activationId, OutputId: output.OutputId, Result: result,
                            Error: (Exception?)null, Lineage: lineage);
                    }
                    catch (WorkflowBodyEvidenceWriteException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        if (error is not OperationCanceledException) outputsCancellation.Cancel();
                        return (ActivationId: activationId, OutputId: output.OutputId, Result: (GraphMessage?)null,
                            Error: error, Lineage: lineage);
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

                var breakOutcome = outcomes.FirstOrDefault(outcome => outcome.Result?.ActivationBreakRequest is not null);
                if (breakOutcome.Result?.ActivationBreakRequest is { } breakRequest)
                {
                    foreach (var completed in outcomes.Where(outcome => outcome.Result is not null))
                        coordinator.Complete(completed.ActivationId, SequenceTerminalState.Completed,
                            completed.Result!.Text);
                    await FlushSequenceEvents();
                    await Emit("sequence.aborted", nodeId, sequenceInvocationId,
                        data: JsonSerializer.Serialize(breakRequest,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    if (dispatchLedger is not null && continuationDelivery is not null)
                        dispatchLedger.Transition(continuationDelivery, []);
                    return breakOutcome.Result;
                }

                foreach (var completed in outcomes)
                    coordinator.Complete(completed.ActivationId, SequenceTerminalState.Completed, completed.Result!.Text);
                await FlushSequenceEvents();
                var deterministicResult = coordinator.DeterministicResult
                    ?? throw new InvalidOperationException("The Sequence produced no completed output value.");
                await Emit(ExecutionEventKinds.NodeCompleted, nodeId, sequenceInvocationId,
                    data: "All Sequence outputs completed.");
                var firstOutputResult = outcomes[0].Result!;
                if (dispatchLedger is not null && continuationDelivery is not null)
                    dispatchLedger.Transition(continuationDelivery, []);
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
            catch (WorkflowBodyEvidenceWriteException)
            {
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
        StructuredActivationRunBudget? runBudget = null,
        IReadOnlySet<string>? proofBreakNodeIds = null)
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
            WorkflowDataTypes.Json => item.GetRawText(),
            _ => throw new ArgumentException("Activation proof input requires a supported array element type.", nameof(arrayElementType))
        }).ToArray();
        var baseParameters = WorkflowParameters.Resolve(bodyRevision.Definition, parameters);
        StructuredActivationGraphPlan? activationGraphPlan = null;
        if (bodyRevision.Definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph &&
            bodyRevision.Definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Loop))
        {
            if (!StructuredActivationGraphPlan.TryCreate(bodyRevision.Definition, out activationGraphPlan,
                    out var topologyIssues))
                throw new InvalidOperationException("The activation body topology could not be compiled: " +
                    string.Join(" ", topologyIssues.Select(issue => issue.Message)));
        }
        var activationFrameOwner = new StructuredActivationFrameOwner(activationGraphPlan, parentFrame,
            bodyRevision.Definition, graphActivationMultiplicity: items.Length);
        var retainedDataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(bodyRevision.Definition)
            ? new ExplicitGraphDataEvaluator(bodyRevision.Definition, items.FirstOrDefault() ?? string.Empty,
                baseParameters, activationFrameOwner.EvidenceBudget)
            : null;
        var variableStores = new ConcurrentDictionary<Guid, WorkflowVariableStore>();
        var session = new StructuredProofSession(executorSetCreated);
        var runContext = new StructuredActivationRunContext(runId, bodyRevision.RevisionId, record, runBudget);
        async ValueTask Emit(string kind, string? nodeId = null, Guid? invocationId = null,
            string? scopeId = null, string? data = null, string? connectionId = null)
        {
            try
            {
                await runContext.EmitAsync(kind, nodeId, invocationId, scopeId, data, connectionId);
            }
            catch (StructuredActivationBudgetExceededException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new WorkflowBodyEvidenceWriteException(exception);
            }
        }

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
                var variableStore = variableStores.GetOrAdd(iterationId,
                    _ => activationFrameOwner.CreateVariableStore(bodyRevision.Definition));
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
                    retainedSession: session, proofBreakNodeIds: proofBreakNodeIds,
                    activationFrameOwner: activationFrameOwner);
                if (dispatchResult.BreakRequest is { } breakRequest)
                {
                    var breakSignal = activationFrameOwner.ConsumeBreak(frame, breakRequest);
                    await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, iterationId,
                        scopeId, JsonSerializer.Serialize(new { outcome = "break", breakSignal.SourceNodeId,
                            breakSignal.TargetFrameId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    break;
                }

                var result = dispatchResult.Execution;
                if (result.OutcomeUnknown)
                {
                    await Emit(ExecutionEventKinds.LoopIterationCompleted, activationLoopNodeId, iterationId,
                        scopeId, JsonSerializer.Serialize(new { outcome = "outcome-unknown" },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    await Emit(ExecutionEventKinds.OutcomeUnknown,
                        data: "Workflow execution ended with an unresolved external outcome.");
                    return new(null, true, Failed: true, OutcomeUnknown: true, HasOutput: false);
                }
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
        catch (Exception exception) when (!ContainsEvidenceWriteFailure(exception))
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
