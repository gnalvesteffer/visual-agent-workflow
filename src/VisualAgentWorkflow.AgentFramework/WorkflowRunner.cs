using System.Collections.Immutable;
using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed record WorkflowExecutionResult(string? Output, bool Terminated, bool Failed = false,
    bool OutcomeUnknown = false);

/// <summary>Host-resolved defaults for bounded collaborative workflow regions.</summary>
public sealed record ParallelCollaborationRuntimeOptions(TimeSpan? DefaultTimeout = null);

/// <summary>Translates the supported string workflow into actual Agent Framework executors and edges.</summary>
public sealed partial class WorkflowRunner(INodeToolBroker? tools = null, IAgentNodeExecutor? agents = null,
    ParallelCollaborationRuntimeOptions? collaborationOptions = null)
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
        WorkflowRevision? rootRevision = null, ImmutableArray<WorkflowApprovalScope> workflowPath = default,
        SequenceProductionPlan? sequencePlan = null, bool sequenceWorkflowCallProof = false,
        bool allowOutcomeUnknown = false)
    {
        var callerCancellationToken = cancellationToken;
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        if (!sequenceWorkflowCallProof)
            WorkflowValidator.ThrowIfInvalid(revision.Definition);
        var collaborationRegion = revision.Definition.ParallelCollaborationRegions?.Values.SingleOrDefault();
        if (revision.Definition.ParallelCollaborationRegions is { Count: > 1 })
            throw new InvalidOperationException("A workflow can execute only one collaborative parallel region in this runtime version.");
        var resolvedCollaborationTimeout = ResolveCollaborationTimeout(collaborationRegion, collaborationOptions);
        var resolvedParameters = WorkflowParameters.Resolve(revision.Definition, parameters);
        var variableStore = new WorkflowVariableStore(revision.Definition);
        rootRevision ??= revision;
        sequencePlan ??= CreateSequenceProductionPlan(revision);
        workflowPath = workflowPath.IsDefault ? [] : workflowPath;
        using var recordGate = new SemaphoreSlim(1, 1);
        var activationLedger = new ExecutionActivationLedger();
        long sequence = 0;
        async ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null, string? data = null, string? scopeId = null)
        {
            await recordGate.WaitAsync();
            try
            {
                var evt = new ExecutionEvent(runId, revision.RevisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                    kind, node, invocation, connection, data)
                { ScopeId = scopeId };
                activationLedger.Observe(evt);
                await record(evt);
            }
            finally { recordGate.Release(); }
        }

        using var collaborationRuntime = collaborationRegion is null ? null :
            new ParallelCollaborationRuntime(runId, revision.RevisionId, collaborationRegion,
                resolvedCollaborationTimeout!.Value, Emit);
        using var linkedCancellation = collaborationRuntime is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, collaborationRuntime.TimeoutToken);
        if (linkedCancellation is not null) cancellationToken = linkedCancellation.Token;
        var workflow = sequencePlan is null
            ? BuildWorkflow(runId, revision, input, resolvedParameters, Emit, gate, rootRevision, workflowPath,
                collaborationRuntime, variableStore)
            : BuildSequenceProductionWorkflow(runId, revision, sequencePlan, Emit, gate, rootRevision, workflowPath);

        await Emit(ExecutionEventKinds.Started);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputType = WorkflowPorts.Outputs(revision.Definition.Nodes[revision.Definition.EntryNodeId]).Single().DataType;
            var execution = sequencePlan is not null || revision.Definition.Nodes.Values.Any(node => node?.Type == NodeTypes.ParallelFork)
                ? InProcessExecution.Concurrent
                : InProcessExecution.Default;
            await using var run = await execution.RunStreamingAsync(workflow,
                new GraphMessage(input, null, null, DataType: inputType), cancellationToken: cancellationToken);
            using var cancellationRegistration = cancellationToken.Register(
                static state => _ = CancelStreamingRunAsync((StreamingRun)state!), run);
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
                activationLedger.ThrowIfActive();
                await Emit(ExecutionEventKinds.Terminated, output.SourceNode);
                return new(null, true);
            }
            activationLedger.ThrowIfActive();
            await Emit(ExecutionEventKinds.Completed, data: output.Text);
            return new(output.Text, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (collaborationRuntime?.IsTimedOut == true && !callerCancellationToken.IsCancellationRequested)
            {
                if (collaborationRuntime is not null) await collaborationRuntime.AbortAsync("The collaboration region timed out.");
                await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
                throw new TimeoutException("The collaboration region timeout elapsed.");
            }
            if (collaborationRuntime is not null) await collaborationRuntime.AbortAsync("Workflow execution was cancelled.");
            await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            if (allowOutcomeUnknown && IsOutcomeUnknownException(exception))
            {
                await Emit(ExecutionEventKinds.OutcomeUnknown, data: "Workflow execution ended with an unresolved external outcome.");
                return new(null, true, Failed: true, OutcomeUnknown: true);
            }
            if (collaborationRuntime is not null) await collaborationRuntime.AbortAsync("A collaboration member or workflow node failed.");
            // Do not copy arbitrary exception messages or provider internals into captured payloads.
            await Emit(ExecutionEventKinds.Failed, data: "Workflow execution failed.");
            throw;
        }
    }

    private static async Task CancelStreamingRunAsync(StreamingRun run)
    {
        try { await run.CancelRunAsync(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static bool IsOutcomeUnknownException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is ToolInvocationException { Disposition: ToolInvocationFailureDisposition.OutcomeUnknown })
                return true;
        return false;
    }

    private static TimeSpan? ResolveCollaborationTimeout(ParallelCollaborationRegion? region,
        ParallelCollaborationRuntimeOptions? options)
    {
        if (region is null) return null;
        var timeout = region.TimeoutSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : options?.DefaultTimeout;
        if (timeout is null || timeout.Value <= TimeSpan.Zero || timeout.Value == Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException("A collaborative parallel region requires a positive workflow timeout or a finite host default timeout.");
        return timeout;
    }

    private Workflow BuildWorkflow(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate,
        WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath, WorkflowVariableStore? variableStore = null) =>
        BuildWorkflow(runId, revision, input, parameters, (kind, node, invocation, connection, data, _) => emit(kind, node, invocation, connection, data), gate, rootRevision, workflowPath, null, variableStore, null);

    private Workflow BuildWorkflow(Guid runId, WorkflowRevision revision, string input,
        IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate,
        WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath,
        ParallelCollaborationRuntime? collaborationRuntime = null, WorkflowVariableStore? variableStore = null,
        CheckpointBoundaryTracker? boundaryTracker = null)
    {
        var definition = revision.Definition;
        var dataEvaluator = WorkflowSchemaVersions.UsesExplicitGraph(definition) ? new ExplicitGraphDataEvaluator(definition, input, parameters) : null;
        var executors = definition.Nodes.ToDictionary(
            n => n.Key,
            n => (Executor<GraphMessage, GraphMessage>)new StringExecutor(n.Key, n.Value, runId, revision, parameters, tools, agents, dataEvaluator, variableStore, emit, gate, null, rootRevision, workflowPath, collaborationOptions, collaborationRuntime, null, boundaryTracker));
        if (collaborationRuntime is not null)
        {
            var region = collaborationRuntime.Region;
            AddRendezvous(region.RendezvousNodeId, ParallelCollaborationPhases.Phase1);
            if (region.SecondRendezvousNodeId is { } secondRendezvousId)
                AddRendezvous(secondRendezvousId, ParallelCollaborationPhases.Phase2);

            void AddRendezvous(string rendezvousId, string phase)
            {
                var arrivals = definition.ExecutionConnections!.Values
                    .Where(edge => edge.Target.NodeId == rendezvousId)
                    .ToDictionary(edge => edge.Target.PortId["branch.".Length..], edge => edge.Source.NodeId, StringComparer.Ordinal);
                var destinations = definition.ExecutionConnections!.Values
                    .Where(edge => edge.Source.NodeId == rendezvousId)
                    .ToDictionary(edge => edge.Source.PortId["branch.".Length..], edge => edge.Target.NodeId, StringComparer.Ordinal);
                executors[rendezvousId] = new ParallelCollaborationRendezvousExecutor(
                    rendezvousId, phase, region, arrivals, destinations, emit, collaborationRuntime);
            }
        }
        var builder = new WorkflowBuilder(executors[definition.EntryNodeId]);
        var executionEdges = WorkflowSchemaVersions.UsesExplicitGraph(definition)
            ? definition.ExecutionConnections!.Values
            : definition.Connections.Values;
        foreach (var edge in executionEdges)
        {
            if (definition.Nodes[edge.Target.NodeId].Type == NodeTypes.ParallelJoin) continue;
            builder.AddEdge<GraphMessage>((ExecutorBinding)executors[edge.Source.NodeId], (ExecutorBinding)executors[edge.Target.NodeId], message =>
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
        bool TerminationSignal = false, bool FailureSignal = false,
        Guid? ParallelActivationId = null, string? ParallelBranchId = null, string? ParallelForkNodeId = null,
        string? CollaborationTargetBranchId = null, bool CollaborationTransition = false,
        SequenceMessageLineage? SequenceLineage = null);

    /// <summary>Private transport identity for a future Sequence activation; never authored or persisted.</summary>
    private sealed record SequenceMessageLineage(Guid SequenceInvocationId, string SequenceNodeId,
        string OutputId, int Ordinal, Guid OutputActivationId, string ConnectionId, string? DescendantScopeId = null);

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
    private sealed class GraphDataEvaluationException(string nodeId, string message, Exception innerException)
        : InvalidOperationException(message, innerException)
    {
        public string NodeId { get; } = nodeId;
    }
    private sealed record EvaluatedDataValue(string NodeId, string PortId, string DataType, object Value, string? ConnectionId,
        object? FunctionEvidence = null);
    private sealed record DataInputEvaluation(ExplicitGraphValue Result, ImmutableArray<EvaluatedDataValue> Evaluations);
    private sealed record GraphFunctionAudit(string FunctionId, int Version, string ContentHash, string OutputId,
        IReadOnlyDictionary<string, object> Inputs, ImmutableArray<GraphFunctionValueEvidence> EvaluatedValues);

    private sealed class ParallelCollaborationRuntime : IDisposable
    {
        private const int EventPayloadVersion = 1;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Guid runId;
        private readonly Guid revisionId;
        private readonly TimeSpan effectiveTimeout;
        private readonly Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit;
        private readonly SemaphoreSlim stateAndEventGate = new(1, 1);
        private readonly CancellationTokenSource timeoutSignal = new();
        private readonly CancellationTokenSource lifetime = new();
        private ParallelCollaborationExchange? phase1Exchange;
        private ParallelCollaborationExchange? phase2Exchange;
        private ParallelCollaborationSnapshot? phase1Snapshot;
        private ParallelCollaborationSnapshot? phase2Snapshot;
        private readonly ParallelCollaborationBudget budget = new();
        private DateTimeOffset? deadline;
        private Task? timeoutTask;
        private bool abortRecorded;
        private string activePhase = ParallelCollaborationPhases.Phase1;

        public ParallelCollaborationRuntime(Guid runId, Guid revisionId, ParallelCollaborationRegion region,
            TimeSpan effectiveTimeout, Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit)
        {
            this.runId = runId;
            this.revisionId = revisionId;
            Region = region;
            this.effectiveTimeout = effectiveTimeout;
            this.emit = emit;
        }

        public ParallelCollaborationRegion Region { get; }
        public bool IsPhased => Region.SecondRendezvousNodeId is not null;
        public CancellationToken TimeoutToken => timeoutSignal.Token;
        public bool IsTimedOut => timeoutSignal.IsCancellationRequested;
        public Guid RegionInvocationId { get; private set; }

        public async ValueTask ActivateAsync(Guid invocationId)
        {
            await stateAndEventGate.WaitAsync(CancellationToken.None);
            try
            {
                if (phase1Exchange is not null) throw new InvalidOperationException("A collaborative region cannot be activated twice in one run.");
                RegionInvocationId = invocationId;
                deadline = DateTimeOffset.UtcNow.Add(effectiveTimeout);
                var branchIds = Region.Branches.Select(branch => branch.BranchId);
                phase1Exchange = new ParallelCollaborationExchange(runId, invocationId, branchIds, Region.ChannelIds, budget);
                if (IsPhased) phase2Exchange = new ParallelCollaborationExchange(runId, invocationId, branchIds, Region.ChannelIds, budget);
                timeoutTask = MonitorTimeoutAsync(effectiveTimeout, lifetime.Token);
                var eventData = IsPhased
                    ? JsonSerializer.Serialize(new
                    {
                        version = 2, revisionId, regionNodeId = Region.RendezvousNodeId,
                        secondRendezvousNodeId = Region.SecondRendezvousNodeId,
                        forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                        regionInvocationId = invocationId, timeoutSeconds = effectiveTimeout.TotalSeconds,
                        deadline = deadline.Value
                    }, JsonOptions)
                    : JsonSerializer.Serialize(new
                    {
                        version = EventPayloadVersion, revisionId, regionNodeId = Region.RendezvousNodeId,
                        forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                        regionInvocationId = invocationId, timeoutSeconds = effectiveTimeout.TotalSeconds,
                        deadline = deadline.Value
                    }, JsonOptions);
                await emit(ExecutionEventKinds.ParallelCollaborationRegionStarted, Region.RendezvousNodeId,
                    invocationId, "collaboration:region", eventData, null);
            }
            finally { stateAndEventGate.Release(); }
        }

        public async ValueTask<string> PublishAsync(string nodeId, string branchId, string phase, string channel,
            string content, Guid invocationId, CancellationToken cancellationToken)
        {
            await stateAndEventGate.WaitAsync(cancellationToken);
            try
            {
                RequireGrant(nodeId, branchId, phase, ParallelCollaborationRoles.Publish, channel);
                cancellationToken.ThrowIfCancellationRequested();
                var sent = RequireExchange(phase).Send(branchId, channel, content);
                var data = IsPhased
                    ? JsonSerializer.Serialize(new { version = 2, phase, regionInvocationId = RegionInvocationId, sent.Sequence, sent.BranchId, sent.Channel, sent.Content }, JsonOptions)
                    : JsonSerializer.Serialize(new { version = EventPayloadVersion, regionInvocationId = RegionInvocationId, sent.Sequence, sent.BranchId, sent.Channel, sent.Content }, JsonOptions);
                await emit(ExecutionEventKinds.ParallelCollaborationMessageSent, nodeId, invocationId,
                    channel, data, null);
                return data;
            }
            finally { stateAndEventGate.Release(); }
        }

        public async ValueTask<string> ReadAsync(string nodeId, string branchId, string phase, string channel,
            Guid invocationId, CancellationToken cancellationToken)
        {
            await stateAndEventGate.WaitAsync(cancellationToken);
            try
            {
                RequireGrant(nodeId, branchId, phase, ParallelCollaborationRoles.Read, channel);
                cancellationToken.ThrowIfCancellationRequested();
                var messages = RequireExchange(phase).ReadSnapshot(branchId, channel);
                var view = JsonSerializer.Serialize(messages, JsonOptions);
                var hash = Snapshot(phase)?.Sha256;
                var data = IsPhased
                    ? JsonSerializer.Serialize(new { version = 2, phase, regionInvocationId = RegionInvocationId, branchId, channel, snapshotSha256 = hash, viewJson = view }, JsonOptions)
                    : JsonSerializer.Serialize(new { version = EventPayloadVersion, regionInvocationId = RegionInvocationId, branchId, channel, snapshotSha256 = hash, viewJson = view }, JsonOptions);
                await emit(ExecutionEventKinds.ParallelCollaborationMessagesRead, nodeId, invocationId,
                    channel, data, null);
                return view;
            }
            finally { stateAndEventGate.Release(); }
        }

        public async ValueTask<ParallelCollaborationArrival> ArriveAsync(string phase, string rendezvousNodeId, string branchId, Guid invocationId,
            CancellationToken cancellationToken)
        {
            await stateAndEventGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arrival = RequireExchange(phase).Arrive(branchId);
                var arrivalData = IsPhased
                    ? JsonSerializer.Serialize(new { version = 2, phase, regionInvocationId = RegionInvocationId, branchId, arrival.ArrivedCount, arrival.ParticipantCount, arrival.Released }, JsonOptions)
                    : JsonSerializer.Serialize(new { version = EventPayloadVersion, regionInvocationId = RegionInvocationId, branchId, arrival.ArrivedCount, arrival.ParticipantCount, arrival.Released }, JsonOptions);
                await emit(ExecutionEventKinds.ParallelCollaborationArrival, rendezvousNodeId,
                    invocationId, $"collaboration:arrival:{branchId}", arrivalData, null);
                if (arrival.Released)
                {
                    var releasedSnapshot = arrival.Snapshot ?? throw new InvalidOperationException("The collaboration rendezvous released without a snapshot.");
                    if (phase == ParallelCollaborationPhases.Phase2) phase2Snapshot = releasedSnapshot;
                    else
                    {
                        phase1Snapshot = releasedSnapshot;
                        activePhase = ParallelCollaborationPhases.Phase2;
                    }
                    var releaseData = IsPhased
                        ? JsonSerializer.Serialize(new { version = 2, phase, regionNodeId = rendezvousNodeId, forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                            regionInvocationId = RegionInvocationId, sha256 = releasedSnapshot.Sha256, utf8Bytes = releasedSnapshot.SerializedBytes, canonicalJson = releasedSnapshot.CanonicalJson }, JsonOptions)
                        : JsonSerializer.Serialize(new { version = EventPayloadVersion, regionNodeId = rendezvousNodeId, forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                            regionInvocationId = RegionInvocationId, sha256 = releasedSnapshot.Sha256, utf8Bytes = releasedSnapshot.SerializedBytes, canonicalJson = releasedSnapshot.CanonicalJson }, JsonOptions);
                    await emit(ExecutionEventKinds.ParallelCollaborationReleased, rendezvousNodeId,
                        invocationId, "collaboration:snapshot", releaseData, null);
                }
                return arrival;
            }
            finally { stateAndEventGate.Release(); }
        }

        public async ValueTask AbortAsync(string reason)
        {
            await stateAndEventGate.WaitAsync(CancellationToken.None);
            try
            {
                if (abortRecorded || phase1Exchange is null) return;
                phase1Exchange.Abort(reason);
                phase2Exchange?.Abort(reason);
                abortRecorded = true;
                var data = IsPhased
                    ? JsonSerializer.Serialize(new
                    {
                        version = 2, phase = activePhase,
                        regionNodeId = activePhase == ParallelCollaborationPhases.Phase2 ? Region.SecondRendezvousNodeId : Region.RendezvousNodeId,
                        forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                        regionInvocationId = RegionInvocationId, reason = phase1Exchange.AbortReason ?? reason,
                        released = phase1Snapshot is not null || phase2Snapshot is not null,
                        snapshotSha256 = phase2Snapshot?.Sha256 ?? phase1Snapshot?.Sha256,
                        releasedPhases = new[] { phase1Snapshot is null ? null : ParallelCollaborationPhases.Phase1,
                            phase2Snapshot is null ? null : ParallelCollaborationPhases.Phase2 }
                    }, JsonOptions)
                    : JsonSerializer.Serialize(new
                    {
                        version = EventPayloadVersion, regionNodeId = Region.RendezvousNodeId,
                        forkNodeId = Region.ForkNodeId, joinNodeId = Region.JoinNodeId,
                        regionInvocationId = RegionInvocationId, reason = phase1Exchange.AbortReason ?? reason,
                        released = phase1Snapshot is not null, snapshotSha256 = phase1Snapshot?.Sha256
                    }, JsonOptions);
                await emit(ExecutionEventKinds.ParallelCollaborationAborted, !IsPhased || activePhase == ParallelCollaborationPhases.Phase1
                        ? Region.RendezvousNodeId : Region.SecondRendezvousNodeId,
                    RegionInvocationId, "collaboration:abort", data, null);
            }
            finally { stateAndEventGate.Release(); }
        }

        private async Task MonitorTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                var remaining = timeout;
                var maxDelay = TimeSpan.FromDays(1);
                while (remaining > TimeSpan.Zero)
                {
                    var delay = remaining < maxDelay ? remaining : maxDelay;
                    await Task.Delay(delay, cancellationToken);
                    remaining -= delay;
                }
                await AbortAsync("The collaboration region timed out.");
                timeoutSignal.Cancel();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private void RequireGrant(string nodeId, string branchId, string phase, string role, string channel)
        {
            var branch = Region.Branches.SingleOrDefault(item => item.BranchId == branchId);
            var grant = branch?.Agents.SingleOrDefault(item => item.AgentNodeId == nodeId && item.Phase == phase &&
                (!IsPhased || item.Role == role));
            if (grant is null || !grant.Channels.Contains(channel, StringComparer.Ordinal))
                throw new InvalidOperationException("The agent invocation has no collaboration grant for this branch, phase, and channel.");
        }

        private ParallelCollaborationExchange RequireExchange(string phase) => (phase == ParallelCollaborationPhases.Phase2
            ? phase2Exchange : phase1Exchange) ?? throw new InvalidOperationException("The collaboration region has not been activated by its fork.");

        private ParallelCollaborationSnapshot? Snapshot(string phase) => phase == ParallelCollaborationPhases.Phase2 ? phase2Snapshot : phase1Snapshot;

        public void Dispose()
        {
            lifetime.Cancel();
            try { timeoutTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            timeoutSignal.Dispose();
            lifetime.Dispose();
            stateAndEventGate.Dispose();
        }
    }

    private sealed class ParallelCollaborationRendezvousExecutor : Executor<GraphMessage, GraphMessage>
    {
        private const string ExecutorPinPrefix = "branch.";
        private readonly string executorId;
        private readonly string phase;
        private readonly ParallelCollaborationRegion region;
        private readonly IReadOnlyDictionary<string, string> arrivalSources;
        private readonly IReadOnlyDictionary<string, string> branchDestinations;
        private readonly Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit;
        private readonly ParallelCollaborationRuntime runtime;
        private readonly object sync = new();
        private readonly Dictionary<string, GraphMessage> arrivals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> arrivalInvocations = new(StringComparer.Ordinal);

        public ParallelCollaborationRendezvousExecutor(string id, string phase, ParallelCollaborationRegion region,
            IReadOnlyDictionary<string, string> arrivalSources, IReadOnlyDictionary<string, string> branchDestinations,
            Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit,
            ParallelCollaborationRuntime runtime) : base(id, declareCrossRunShareable: true)
        {
            executorId = id;
            this.phase = phase;
            this.region = region;
            this.arrivalSources = arrivalSources;
            this.branchDestinations = branchDestinations;
            this.emit = emit;
            this.runtime = runtime;
        }

        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var branchId = message.ParallelBranchId ?? throw new InvalidOperationException("A collaboration arrival has no stable branch ID.");
            if (!arrivalSources.TryGetValue(branchId, out var expectedSource) || message.SourceNode != expectedSource ||
                message.ParallelForkNodeId != region.ForkNodeId || message.ParallelActivationId != runtime.RegionInvocationId)
                throw new InvalidOperationException("The collaboration rendezvous received an unexpected branch message.");

            var invocationId = Guid.NewGuid();
            try
            {
                await emit(ExecutionEventKinds.NodeStarted, executorId, invocationId, null, "Waiting for every collaboration branch.", null);
                lock (sync)
                {
                    if (!arrivals.TryAdd(branchId, message)) throw new InvalidOperationException("A collaboration branch arrived more than once.");
                    arrivalInvocations.Add(branchId, invocationId);
                }
                var arrival = await runtime.ArriveAsync(phase, executorId, branchId, invocationId, cancellationToken);
                if (!arrival.Released)
                    return message with { SourceNode = executorId, SourcePort = "pending" };

                GraphMessage[] allArrivals;
                Guid[] allInvocations;
                lock (sync)
                {
                    allArrivals = region.Branches.Select(branch => arrivals[branch.BranchId]).ToArray();
                    allInvocations = region.Branches.Select(branch => arrivalInvocations[branch.BranchId]).ToArray();
                }
                foreach (var branch in region.Branches)
                {
                    if (!branchDestinations.TryGetValue(branch.BranchId, out var target))
                        throw new InvalidOperationException($"The collaboration branch '{branch.BranchId}' has no post-rendezvous route.");
                    var branchIndex = region.Branches.IndexOf(branch);
                    var source = allArrivals[branchIndex];
                    await context.SendMessageAsync(source with
                    {
                        SourceNode = executorId,
                        SourcePort = $"{ExecutorPinPrefix}{branch.BranchId}",
                        CollaborationTargetBranchId = branch.BranchId,
                        CollaborationTransition = true
                    }, target, cancellationToken);
                    await emit(ExecutionEventKinds.NodeCompleted, executorId, allInvocations[branchIndex],
                        $"collaboration:{branch.BranchId}", runtime.IsPhased ? $"The {phase} snapshot was released." : "The shared snapshot was released.", null);
                }
                return message with { SourceNode = executorId, SourcePort = "released" };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await emit(ExecutionEventKinds.NodeCancelled, executorId, invocationId, null, "Collaboration rendezvous cancelled.", null);
                throw;
            }
            catch
            {
                await emit(ExecutionEventKinds.NodeFailed, executorId, invocationId, null, "Collaboration rendezvous failed.", null);
                throw;
            }
        }
    }

    /// <summary>Evaluates only bounded, pure explicit-graph data nodes in one activation context.</summary>
    private sealed class ExplicitGraphDataEvaluator(WorkflowDefinition definition, string input,
        IReadOnlyDictionary<string, string>? parameters)
    {
        private int functionNodeVisits;
        private readonly ConcurrentDictionary<(string NodeId, string PortId), GraphFunctionAudit> functionAudits = new();
        private readonly ConcurrentDictionary<(string NodeId, string PortId), ExplicitGraphValue> runtimeValues = new();

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
                ExplicitGraphValue result;
                try
                {
                    result = node.Type switch
                    {
                    NodeTypes.Input when outputPort == "out" => new ExplicitGraphValue("string", input),
                    NodeTypes.WorkflowValue when outputPort == "out" => ResolveParameter(node),
                    NodeTypes.StringConstant when outputPort == "out" => new ExplicitGraphValue("string", node.StringConstant!.Value),
                    NodeTypes.TextConcat when outputPort == "out" => new ExplicitGraphValue("string", Concatenate(nodeId, "left", "right", evaluated, cache, active)),
                    NodeTypes.Uppercase when outputPort == "out" => new ExplicitGraphValue("string", ResolveStringInput(nodeId, "in", evaluated, cache, active).ToUpperInvariant()),
                    NodeTypes.Reverse when outputPort == "out" => new ExplicitGraphValue("string", string.Concat(ResolveStringInput(nodeId, "in", evaluated, cache, active).EnumerateRunes().Reverse())),
                    NodeTypes.StringEquals when outputPort == "out" => new ExplicitGraphValue("boolean",
                        string.Equals(ResolveStringInput(nodeId, "left", evaluated, cache, active),
                            ResolveStringInput(nodeId, "right", evaluated, cache, active), StringComparison.Ordinal)),
                    NodeTypes.IntegerConstant when outputPort == "out" => new ExplicitGraphValue("integer", node.IntegerConstant!.Value),
                    NodeTypes.NumberConstant when outputPort == "out" && node.NumberConstant is { Value: var value } && double.IsFinite(value) => new ExplicitGraphValue("number", value),
                    NodeTypes.NumberAdd when outputPort == "out" => new ExplicitGraphValue("number", AddNumbers(nodeId, evaluated, cache, active)),
                    NodeTypes.StringToInteger when outputPort == "out" => ConvertStringToInteger(nodeId, evaluated, cache, active),
                    NodeTypes.IntegerAdd when outputPort == "out" => new ExplicitGraphValue("integer", AddIntegers(nodeId, evaluated, cache, active)),
                    NodeTypes.IntegerEquals when outputPort == "out" => new ExplicitGraphValue("boolean", IntegersEqual(nodeId, evaluated, cache, active)),
                    NodeTypes.BooleanConstant when outputPort == "out" => new ExplicitGraphValue("boolean",
                        node.BooleanConstant?.Value ?? throw new InvalidOperationException("The Boolean constant is not configured.")),
                    NodeTypes.BooleanNot when outputPort == "out" => new ExplicitGraphValue("boolean",
                        !ResolveBooleanInput(nodeId, "value", evaluated, cache, active)),
                    NodeTypes.FunctionCall when node.GraphFunction is { } function && function.Definition.Outputs.TryGetValue(outputPort, out var output) =>
                        EvaluateFunction(nodeId, outputPort, output.DataType, function, evaluated, cache, active),
                        _ => throw new InvalidOperationException("The data source is not a supported pure value node.")
                    };
                }
                catch (OverflowException exception) when (node.Type == NodeTypes.IntegerAdd)
                {
                    throw new GraphDataEvaluationException(nodeId,
                        "Integer addition exceeded the signed 64-bit range.", exception);
                }
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

        private ExplicitGraphValue ResolveParameter(NodeDefinition node)
        {
            if (node.ParameterId is not { } parameterId || parameters is null || !parameters.TryGetValue(parameterId, out var value) ||
                definition.Parameters is not { } declared || !declared.TryGetValue(parameterId, out var parameter))
                throw new InvalidOperationException("The workflow parameter value is unavailable.");
            if (definition.SchemaVersion != WorkflowSchemaVersions.IntegerGraph && definition.SchemaVersion != WorkflowSchemaVersions.NumberPropagationGraph && definition.SchemaVersion != WorkflowSchemaVersions.NumberArithmeticGraph)
                return new("string", value);
            var allowNumber = definition.SchemaVersion is WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph;
            var dataType = WorkflowParameterGraphTypes.DataType(parameter, allowNumber);
            if (dataType is null || node.OutputDataType is { } expectedType && expectedType != dataType)
                throw new InvalidOperationException("The workflow parameter has an unsupported graph data type.");
            object parsed = dataType switch
            {
                "integer" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => integer,
                "number" when WorkflowParameters.TryParseFiniteNumber(value, out var number) => number,
                "boolean" when bool.TryParse(value, out var boolean) => boolean,
                "string" => value,
                _ => throw new InvalidOperationException("The workflow parameter value is invalid.")
            };
            return new(dataType, parsed);
        }

        private long AddIntegers(string nodeId,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var left = ResolveIntegerInput(nodeId, "left", evaluated, cache, active);
            var right = ResolveIntegerInput(nodeId, "right", evaluated, cache, active);
            return checked(left + right);
        }

        private ExplicitGraphValue ConvertStringToInteger(string nodeId,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var input = ResolveStringInput(nodeId, "in", evaluated, cache, active);
            var result = StringIntegerConversion.Parse(input);
            if (!result.Success)
                throw new GraphDataEvaluationException(nodeId,
                    JsonSerializer.Serialize(new
                    {
                        code = result.Failure?.Code,
                        parserVersion = result.Failure?.ParserVersion,
                        reason = result.Failure?.Reason,
                        inputLength = result.Failure?.InputLength
                    }),
                    new InvalidOperationException(result.Failure?.Reason ?? "invalid"));
            return new ExplicitGraphValue(WorkflowDataTypes.Integer, result.Value!.Value);
        }

        private double AddNumbers(string nodeId,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var left = ResolveNumberInput(nodeId, "left", evaluated, cache, active);
            var right = ResolveNumberInput(nodeId, "right", evaluated, cache, active);
            var result = left + right;
            if (!double.IsFinite(result))
                throw new GraphDataEvaluationException(nodeId, "number.add.nonfinite-result",
                    new InvalidOperationException("Number addition overflowed to a non-finite result."));
            return result;
        }

        private bool IntegersEqual(string nodeId,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active) =>
            ResolveIntegerInput(nodeId, "left", evaluated, cache, active) ==
            ResolveIntegerInput(nodeId, "right", evaluated, cache, active);

        private long ResolveIntegerInput(string nodeId, string inputPort,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == inputPort);
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != "integer" || value.Value is not long integer)
                throw new InvalidOperationException("The pure data input requires a signed 64-bit integer.");
            return integer;
        }

        private double ResolveNumberInput(string nodeId, string inputPort,
            ImmutableArray<EvaluatedDataValue>.Builder evaluated,
            Dictionary<(string NodeId, string PortId), ExplicitGraphValue> cache,
            HashSet<(string NodeId, string PortId)> active)
        {
            var edge = definition.Connections.Single(connection => connection.Value.Target.NodeId == nodeId && connection.Value.Target.PortId == inputPort);
            var value = ResolveOutput(edge.Value.Source.NodeId, edge.Value.Source.PortId, edge.Key, evaluated, cache, active);
            if (value.DataType != WorkflowDataTypes.Number || value.Value is not double number || !double.IsFinite(number))
                throw new InvalidOperationException("The pure data input requires a finite IEEE-754 double.");
            return number;
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
        WorkflowVariableStore? variableStore,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> record, IWorkflowExecutionGate? gate,
        string? scopeId, WorkflowRevision rootRevision, ImmutableArray<WorkflowApprovalScope> workflowPath,
        ParallelCollaborationRuntimeOptions? collaborationOptions,
        ParallelCollaborationRuntime? collaborationRuntime = null,
        Func<SequenceMessageLineage, SequenceTerminalState, ValueTask>? recordSequenceTerminal = null,
        CheckpointBoundaryTracker? boundaryTracker = null,
        SequenceWorkflowCallSettlement? callSettlement = null,
        bool allowOutcomeUnknown = false)
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
        private Guid? configurableParallelActivationId;
        private Guid? parallelJoinInvocationId;
        private bool UsesConfigurableParallelGraph => revision.Definition.SchemaVersion is
            WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or
            WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph;

        protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken cancellationToken)
        {
            boundaryTracker?.OnCheckpointing(Id);
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken cancellationToken)
        {
            boundaryTracker?.OnCheckpointRestored(Id);
            return ValueTask.CompletedTask;
        }

        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage received, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            var internalTerminationHandoff = node.Type == NodeTypes.Output && received.TerminationSignal;
            if (gate is not null && !internalTerminationHandoff) await gate.WaitForBoundaryAsync(Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var message = received.Text;
            var invocation = node.Type == NodeTypes.ParallelJoin &&
                UsesConfigurableParallelGraph && received.ParallelActivationId is { } activation
                ? activation
                : node.Type == NodeTypes.ParallelJoin ? ParallelJoinInvocation() : received.Approval?.TargetInvocationId ?? Guid.NewGuid();
            // Receiving the downstream message is direct evidence of delivery, not inferred completion.
            string? incomingPort = null;
            DataInputEvaluation? outputEvaluation = null;
            DataInputEvaluation? branchEvaluation = null;
            DataInputEvaluation? variableSetEvaluation = null;
            var toolArgumentEvaluations = new List<DataInputEvaluation>();
            var configuredAgentRoutes = node.Type == NodeTypes.Agent ? node.Agent?.Routes : null;
            var routeSelection = configuredAgentRoutes is { IsDefaultOrEmpty: false } routes
                ? new AgentRouteSelectionState(routes)
                : null;
            string? agentRouteId = null;
            string? AddSequenceLineage(string? data)
            {
                if (received.SequenceLineage is not { } sequenceLineage) return data;
                if (string.IsNullOrWhiteSpace(data))
                    return JsonSerializer.Serialize(new { eventData = data, sequenceLineage });
                try
                {
                    if (JsonNode.Parse(data) is JsonObject payload)
                    {
                        payload["sequenceLineage"] = JsonSerializer.SerializeToNode(sequenceLineage);
                        return payload.ToJsonString();
                    }
                }
                catch (JsonException) { }
                return JsonSerializer.Serialize(new { eventData = data, sequenceLineage });
            }
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
                    var transferEvidence = received.SequenceLineage is { } sequenceLineage
                        ? JsonSerializer.Serialize(new { sequenceLineage, branchId = received.ParallelBranchId,
                            parallelActivationId = received.ParallelActivationId, forkNodeId = received.ParallelForkNodeId })
                        : revision.Definition.SchemaVersion == WorkflowSchemaVersions.BranchLocalParallelGraph && received.ParallelBranchId is { } transferredBranch
                        ? JsonSerializer.Serialize(new
                        {
                            parallelActivationId = received.ParallelActivationId,
                            branchId = transferredBranch,
                            forkNodeId = received.ParallelForkNodeId
                        })
                        : null;
                    await Emit(ExecutionEventKinds.ExecutionActivated, Id, invocation, $"execution:{incoming.Key}", transferEvidence);
                    incomingPort = incoming.Value.Target.PortId;
                }
                else
                {
                    var incoming = revision.Definition.Connections.Single(edge => edge.Value.Source.NodeId == received.SourceNode &&
                        edge.Value.Source.PortId == received.SourcePort && edge.Value.Target.NodeId == Id);
                    var inputType = WorkflowPorts.Inputs(node).Single(port => port.Id == incoming.Value.Target.PortId).DataType;
                    if (received.DataType != inputType) throw new InvalidOperationException("Connected workflow data type does not match the receiving node.");
                    var transferData = received.SequenceLineage is { } sequenceLineage
                        ? JsonSerializer.Serialize(new { value = message, sequenceLineage })
                        : message;
                    await Emit(ExecutionEventKinds.Transfer, Id, invocation, incoming.Key, transferData);
                    incomingPort = incoming.Value.Target.PortId;
                }
            }
            if (node.Type == NodeTypes.ParallelJoin)
            {
                await parallelJoinExecution.WaitAsync(cancellationToken);
                try
                {
                    var configurable = UsesConfigurableParallelGraph;
                    var joinResult = configurable
                        ? CollectConfigurableParallelJoinInput(incomingPort!, received)
                        : CollectParallelJoinInput(incomingPort!, message) is var legacy
                            ? (legacy.Started, legacy.Complete, legacy.Result, legacy.ReceivedCount, ParallelGraphLimits.MaximumBranches)
                            : throw new InvalidOperationException("Parallel join could not read its branch result.");
                    var (started, complete, result, receivedCount, expectedCount) = joinResult;
                    var progress = configurable
                        ? complete ? $"All {expectedCount} branch results received." : $"Received {receivedCount} of {expectedCount} branch results; waiting for the others."
                        : complete ? "Both branch results received." : $"Received {receivedCount} of 2 branch results; waiting for the other branch.";
                    if (started) await Emit(ExecutionEventKinds.NodeStarted, Id, invocation,
                        data: configurable ? $"Waiting for {expectedCount} parallel branch results." : "Waiting for both parallel branch results.");
                    await Emit(ExecutionEventKinds.ParallelJoinProgress, Id, invocation, data: progress);
                    if (!complete) return new("", Id, "parallel.pending", DataType: "json");
                    if (configurable)
                    {
                        using var document = JsonDocument.Parse(result!);
                        dataEvaluator!.SetRuntimeOutput(Id, "out", "json", document.RootElement.Clone());
                    }
                    await Emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, result);
                    return new(result!, Id, configurable ? "then" : "out", DataType: "json",
                        ParallelActivationId: received.ParallelActivationId, ParallelForkNodeId: received.ParallelForkNodeId,
                        SequenceLineage: received.SequenceLineage);
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
            boundaryTracker?.BeginHandler(Id, received.SourceNode, received.SourcePort, received.LoopIteration);
            try
            {
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.Output &&
                    !internalTerminationHandoff && received.SequenceLineage is null)
                {
                    outputEvaluation = dataEvaluator!.ResolveInput(Id, "in");
                    message = outputEvaluation.Result.DataType switch
                    {
                        "string" when outputEvaluation.Result.Value is string outputValue => outputValue,
                        "integer" when outputEvaluation.Result.Value is long integerValue => integerValue.ToString(CultureInfo.InvariantCulture),
                        "boolean" when outputEvaluation.Result.Value is bool booleanValue => booleanValue ? "true" : "false",
                        "json" when outputEvaluation.Result.Value is JsonElement jsonValue && jsonValue.ValueKind == JsonValueKind.Object => jsonValue.GetRawText(),
                        _ => throw new InvalidOperationException("An Output node requires a string, integer, Boolean, or bounded JSON object value.")
                    };
                }
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.BooleanBranch)
                    branchEvaluation = dataEvaluator!.ResolveBranchCondition(Id);
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.VariableSet)
                    variableSetEvaluation = dataEvaluator!.ResolveInput(Id, "value");
                foreach (var value in (outputEvaluation?.Evaluations ?? []).Concat(branchEvaluation?.Evaluations ?? []).Concat(variableSetEvaluation?.Evaluations ?? []))
                    await Emit(ExecutionEventKinds.DataEvaluated, value.NodeId, invocation,
                        value.ConnectionId is null ? null : $"data:{value.ConnectionId}", SerializeDataEvidence(Id, value));
                WorkflowExecutionResult? childOutcome = null;
                if (node.Type == NodeTypes.WorkflowCall)
                {
                    callSettlement?.TryStart();
                    try
                    {
                        childOutcome = await ExecuteWorkflowCall(message, cancellationToken);
                        callSettlement?.TrySettleChild(childOutcome.OutcomeUnknown
                            ? SequenceWorkflowCallSettlementState.Unknown
                            : childOutcome.Failed
                                ? SequenceWorkflowCallSettlementState.Failed
                                : SequenceWorkflowCallSettlementState.Completed);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        callSettlement?.TrySettleChild(SequenceWorkflowCallSettlementState.Cancelled);
                        throw;
                    }
                    catch
                    {
                        callSettlement?.TrySettleChild(SequenceWorkflowCallSettlementState.Failed);
                        throw;
                    }
                }
                if (recordSequenceTerminal is not null && received.SequenceLineage is { } childLineage &&
                    childOutcome is { Failed: true })
                    await recordSequenceTerminal(childLineage, SequenceTerminalState.Failed);
                else if (recordSequenceTerminal is not null && received.SequenceLineage is { } terminatedLineage &&
                         childOutcome is { Terminated: true })
                    await recordSequenceTerminal(terminatedLineage, SequenceTerminalState.Completed);
                if (node.Type == NodeTypes.VariableSet)
                {
                    if (variableStore is null || variableSetEvaluation is null || node.VariableId is not { } variableId)
                        throw new InvalidOperationException("The variable store or Set value is unavailable.");
                    if (!variableStore.TryRead(variableId, out var previous))
                        throw new InvalidOperationException($"Variable '{variableId}' is not declared.");
                    var written = variableStore.Write(variableId, variableSetEvaluation.Result.DataType, variableSetEvaluation.Result.Value, cancellationToken);
                    await Emit(ExecutionEventKinds.VariableWritten, Id, invocation, null, JsonSerializer.Serialize(new
                    {
                        variableId,
                        dataType = written.DataType,
                        previousValue = previous.Value,
                        value = written.Value,
                        version = written.Version,
                        loopIteration = received.LoopIteration
                    }));
                }
                if (node.Type == NodeTypes.VariableGet)
                {
                    if (variableStore is null || dataEvaluator is null || node.VariableId is not { } variableId || !variableStore.TryRead(variableId, out var current))
                        throw new InvalidOperationException("The variable store or Get variable is unavailable.");
                    dataEvaluator.SetRuntimeOutput(Id, "out", current.DataType, current.Value);
                    await Emit(ExecutionEventKinds.VariableRead, Id, invocation, null, JsonSerializer.Serialize(new
                    {
                        variableId,
                        dataType = current.DataType,
                        value = current.Value,
                        version = current.Version,
                        loopIteration = received.LoopIteration
                    }));
                }
                var result = node.Type switch
                {
                    NodeTypes.Input when node.InputDataType == "json" => NormalizeJson(message),
                    NodeTypes.BooleanBranch => string.Empty,
                    NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Approval or NodeTypes.Loop or NodeTypes.LoopBody or NodeTypes.VariableGet or NodeTypes.VariableSet => message,
                    NodeTypes.Terminate => string.Empty,
                    NodeTypes.JsonSelectString => SelectJsonText(message, node.JsonPropertySelection!.Pointer),
                    NodeTypes.JsonProject => ProjectJson(message, node.JsonProjection!),
                    NodeTypes.ParallelBranch => message,
                    NodeTypes.ParallelFork => message,
                    NodeTypes.TextTemplate => PromptRendering.Render(node.TextTemplate!, message, parameters),
                    NodeTypes.WorkflowValue => parameters is null || !parameters.TryGetValue(node.ParameterId!, out var value)
                        ? throw new InvalidOperationException("The workflow parameter was not resolved for this run.") : value,
                    NodeTypes.Uppercase => message.ToUpperInvariant(),
                    NodeTypes.SequenceUppercase => message.ToUpperInvariant(),
                    NodeTypes.SequenceReverse => string.Concat(message.EnumerateRunes().Reverse()),
                    NodeTypes.Reverse => string.Concat(message.EnumerateRunes().Reverse()),
                    NodeTypes.McpTool => revision.Definition.SchemaVersion is WorkflowSchemaVersions.DirectMcpGraph or WorkflowSchemaVersions.DirectMcpPinsGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph or WorkflowSchemaVersions.NumberGraph or WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph
                        ? await InvokeDirectMcpTool(message, invocation, toolArgumentEvaluations, cancellationToken)
                        : await InvokeTool(message, invocation, cancellationToken),
                    NodeTypes.WorkflowCall => childOutcome?.Output ?? string.Empty,
                    NodeTypes.Agent => agents is null ? throw new InvalidOperationException("No agent executor is configured.") :
                        await agents.ExecuteAsync(new NodeInvocationContext(runId, revision, Id, invocation,
                            (kind, data) => Emit(kind, Id, invocation, null, AddSequenceLineage(data)))
                        {
                            Parameters = parameters,
                            RecordWithInvocation = (eventInvocationId, kind, data) => Emit(kind, Id, eventInvocationId, null, AddSequenceLineage(data)),
                        CollaborationPhase = CollaborationGrant(Id, received.ParallelBranchId)?.Phase,
                        CollaborationRole = CollaborationGrant(Id, received.ParallelBranchId) is { } collaborationGrant
                            ? collaborationRuntime!.IsPhased ? collaborationGrant.Role : collaborationGrant.Phase
                            : null,
                            ProposeAgentRoute = routeSelection is null ? null : async routeId =>
                            {
                                await Emit(ExecutionEventKinds.AgentRouteProposed, Id, invocation, null, JsonSerializer.Serialize(new { routeId }));
                                if (!routeSelection.Propose(routeId))
                                    await Emit(ExecutionEventKinds.AgentRouteRejected, Id, invocation, null,
                                        JsonSerializer.Serialize(new { routeId, reason = routeSelection.RejectionReason }));
                            },
                            CollaborationSendChannels = CollaborationChannels(Id, received.ParallelBranchId, ParallelCollaborationRoles.Publish),
                            CollaborationReadChannels = CollaborationChannels(Id, received.ParallelBranchId, ParallelCollaborationRoles.Read),
                            CollaborationSend = collaborationRuntime is null || received.ParallelBranchId is not { } publishBranch ||
                                CollaborationGrant(Id, publishBranch) is not { } publishGrant ||
                                (collaborationRuntime.IsPhased ? publishGrant.Role != ParallelCollaborationRoles.Publish : publishGrant.Phase != ParallelCollaborationPhases.Publish) ? null :
                                (channel, content, token) => collaborationRuntime.PublishAsync(Id, publishBranch, publishGrant.Phase, channel, content, invocation, token),
                            CollaborationRead = collaborationRuntime is null || received.ParallelBranchId is not { } readBranch ||
                                CollaborationGrant(Id, readBranch) is not { } readGrant ||
                                (collaborationRuntime.IsPhased ? readGrant.Role != ParallelCollaborationRoles.Read : readGrant.Phase != ParallelCollaborationPhases.Read) ? null :
                                (channel, token) => collaborationRuntime.ReadAsync(Id, readBranch, readGrant.Phase, channel, invocation, token)
                        }, message, cancellationToken),
                    _ => throw new InvalidOperationException("Unsupported executor type.")
                };
                foreach (var evaluation in toolArgumentEvaluations.SelectMany(item => item.Evaluations))
                    await Emit(ExecutionEventKinds.DataEvaluated, evaluation.NodeId, invocation,
                        evaluation.ConnectionId is null ? null : $"data:{evaluation.ConnectionId}", SerializeDataEvidence(Id, evaluation));
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.Agent)
                {
                    var structuredResult = node.Agent?.RequireStructuredResult == true;
                    object runtimeValue = structuredResult
                        ? JsonDocument.Parse(result).RootElement.Clone()
                        : result;
                    dataEvaluator?.SetRuntimeOutput(Id, "out", structuredResult ? "json" : "string", runtimeValue);
                }
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.McpTool)
                    dataEvaluator?.SetRuntimeOutput(Id, "out", "string", result);
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
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type == NodeTypes.McpTool) port = "then";
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type is NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse) port = "then";
                if (WorkflowSchemaVersions.UsesExplicitGraph(revision.Definition) && node.Type is NodeTypes.VariableGet or NodeTypes.VariableSet or NodeTypes.LoopBody) port = "then";
                if (UsesConfigurableParallelGraph && node.Type == NodeTypes.ParallelBranch) port = "then";
                if (node.Type == NodeTypes.BooleanBranch)
                {
                    var condition = branchEvaluation!.Result;
                    port = (bool)condition.Value ? "true" : "false";
                    var decisionEvidence = revision.Definition.SchemaVersion == WorkflowSchemaVersions.BranchLocalParallelGraph && received.ParallelBranchId is { } decisionBranch
                        ? JsonSerializer.Serialize(new
                        {
                            port,
                            parallelActivationId = received.ParallelActivationId,
                            branchId = decisionBranch,
                            forkNodeId = received.ParallelForkNodeId
                        })
                        : JsonSerializer.Serialize(new { port });
                    await Emit(ExecutionEventKinds.BranchSelected, Id, invocation, null, decisionEvidence);
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
                    var configurable = UsesConfigurableParallelGraph;
                    var activationId = configurable ? invocation : (Guid?)null;
                    if (collaborationRuntime is not null && collaborationRuntime.Region.ForkNodeId == Id)
                        await collaborationRuntime.ActivateAsync(invocation);
                    var outgoingEdges = configurable
                        ? revision.Definition.ExecutionConnections!.Values.Where(edge => edge.Source.NodeId == Id)
                        : revision.Definition.Connections.Values.Where(edge => edge.Source.NodeId == Id);
                    foreach (var edge in outgoingEdges)
                    {
                        var branchId = configurable && edge.Source.PortId.StartsWith("branch.", StringComparison.Ordinal)
                            ? edge.Source.PortId["branch.".Length..] : null;
                        await context.SendMessageAsync(new GraphMessage(result, Id, edge.Source.PortId, DataType: "string",
                            ParallelActivationId: activationId, ParallelBranchId: branchId,
                            ParallelForkNodeId: configurable ? Id : null,
                            SequenceLineage: received.SequenceLineage), edge.Target.NodeId, cancellationToken);
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
                var next = revision.Definition.ExecutionConnections?.Values.FirstOrDefault(edge =>
                    edge.Source.NodeId == Id && edge.Source.PortId == port);
                boundaryTracker?.PublishTransition(Id, port, next?.Target.NodeId, next?.Target.PortId, nextLoopIteration);
                return new(result, internalTerminationHandoff ? received.SourceNode : Id, terminated ? "terminated" : port, permit, outputType, nextLoopIteration,
                    terminated, failureSignal, received.ParallelActivationId, received.ParallelBranchId, received.ParallelForkNodeId,
                    SequenceLineage: received.SequenceLineage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                boundaryTracker?.AbortHandler();
                if (recordSequenceTerminal is not null && received.SequenceLineage is { } sequenceLineage)
                    await recordSequenceTerminal(sequenceLineage, SequenceTerminalState.Cancelled);
                await Emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null, "Node execution cancelled.");
                throw;
            }
            catch (JsonProjectionContractException exception)
            {
                boundaryTracker?.AbortHandler();
                if (recordSequenceTerminal is not null && received.SequenceLineage is { } sequenceLineage)
                    await recordSequenceTerminal(sequenceLineage, SequenceTerminalState.Failed);
                await Emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, exception.Message);
                throw;
            }
            catch (GraphDataEvaluationException exception)
            {
                boundaryTracker?.AbortHandler();
                if (recordSequenceTerminal is not null && received.SequenceLineage is { } sequenceLineage)
                    await recordSequenceTerminal(sequenceLineage, SequenceTerminalState.Failed);
                await Emit(ExecutionEventKinds.NodeFailed, exception.NodeId, invocation, null, exception.Message);
                await Emit(ExecutionEventKinds.NodeFailed, Id, invocation, null, "Node execution failed while evaluating a connected data value.");
                throw;
            }
            catch
            {
                boundaryTracker?.AbortHandler();
                if (recordSequenceTerminal is not null && received.SequenceLineage is { } sequenceLineage)
                    await recordSequenceTerminal(sequenceLineage, SequenceTerminalState.Failed);
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

        private ParallelCollaborationAgentGrant? CollaborationGrant(string nodeId, string? branchId)
        {
            if (collaborationRuntime is null || branchId is null) return null;
            return collaborationRuntime.Region.Branches.SingleOrDefault(branch => branch.BranchId == branchId)?
                .Agents.SingleOrDefault(grant => grant.AgentNodeId == nodeId);
        }

        private IReadOnlyList<string>? CollaborationChannels(string nodeId, string? branchId, string role) =>
            CollaborationGrant(nodeId, branchId) is { } grant &&
            (collaborationRuntime!.IsPhased ? grant.Role == role : grant.Phase == role)
                ? grant.Channels : null;

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
                    ExecutionEventKinds.OutcomeUnknown => ExecutionEventKinds.SubworkflowOutcomeUnknown,
                    _ => childEvent.Kind
                };
                var nestedScope = childEvent.ScopeId is null ? childScope : $"{childScope}/{childEvent.ScopeId}";
                await Emit(kind, childEvent.NodeId, childEvent.InvocationId, childEvent.ConnectionId, childEvent.Data, nestedScope);
                if (kind is ExecutionEventKinds.SubworkflowStarted or ExecutionEventKinds.SubworkflowCompleted or
                    ExecutionEventKinds.SubworkflowTerminated or ExecutionEventKinds.SubworkflowFailed or
                    ExecutionEventKinds.SubworkflowCancelled or ExecutionEventKinds.SubworkflowOutcomeUnknown)
                    await Emit(ExecutionEventKinds.SubworkflowEvidence, Id, childEvent.InvocationId,
                        childEvent.ConnectionId,
                        new WorkflowCallLifecycleEvidence(kind, Id, child.Definition.WorkflowId, child.RevisionId,
                            child.ContentHash, nestedScope, childEvent.Data).Serialize(), nestedScope);
            }

            var runner = new WorkflowRunner(tools, agents, collaborationOptions);
            var childWorkflowPath = workflowPath.Add(new WorkflowApprovalScope(Id, child.RevisionId, child.ContentHash));
            IReadOnlyDictionary<string, string>? childParameters = call.ParameterMappings is { Length: > 0 } mappings
                ? mappings.ToDictionary(mapping => mapping.ChildParameterId, mapping => parameters![mapping.ParentParameterId], StringComparer.Ordinal)
                : null;
            return gate is null
                ? await runner.ExecuteCoreAsync(runId, child, input, childParameters, RecordChild, null, cancellationToken,
                    rootRevision, childWorkflowPath, allowOutcomeUnknown: allowOutcomeUnknown)
                : await runner.ExecuteCoreAsync(runId, child, input, childParameters, RecordChild, gate, cancellationToken,
                    rootRevision, childWorkflowPath, allowOutcomeUnknown: allowOutcomeUnknown);
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

        private (bool Started, bool Complete, string? Result, int ReceivedCount, int ExpectedCount)
            CollectConfigurableParallelJoinInput(string inputPort, GraphMessage message)
        {
            var configuration = node.ParallelJoin ?? throw new InvalidOperationException("The parallel join has no branch configuration.");
            var fork = revision.Definition.Nodes[configuration.ForkNodeId];
            var expectedBranches = configuration.BranchIds.ToHashSet(StringComparer.Ordinal);
            var branchId = inputPort.StartsWith("branch.", StringComparison.Ordinal) ? inputPort["branch.".Length..] : "";
            if (message.ParallelActivationId is not { } activationId || message.ParallelForkNodeId != configuration.ForkNodeId ||
                message.ParallelBranchId != branchId || !expectedBranches.Contains(branchId) || fork.Type != NodeTypes.ParallelFork)
                throw new InvalidOperationException("The parallel join received a result from an unexpected fork or branch.");

            lock (parallelJoinSync)
            {
                if (configurableParallelActivationId is { } existingActivation && existingActivation != activationId)
                    throw new InvalidOperationException("A parallel join cannot combine results from different fork activations.");
                configurableParallelActivationId = activationId;
                if (!parallelJoinInputs.TryAdd(branchId, message.Text))
                    throw new InvalidOperationException("The parallel join received a duplicate branch result.");

                var started = parallelJoinInputs.Count == 1;
                if (parallelJoinInputs.Count < expectedBranches.Count)
                    return (started, false, null, parallelJoinInputs.Count, expectedBranches.Count);

                var result = JsonSerializer.Serialize(parallelJoinInputs);
                if (result.Length > ParallelGraphLimits.MaximumAggregateCharacters)
                    throw new InvalidOperationException($"Parallel branch results exceed {ParallelGraphLimits.MaximumAggregateCharacters} characters.");
                return (started, true, result, parallelJoinInputs.Count, expectedBranches.Count);
            }
        }

        private async Task<string> InvokeTool(string input, Guid invocation, CancellationToken cancellationToken)
        {
            if (tools is null) throw new InvalidOperationException("No tool broker is configured.");
            await using var scope = tools.OpenScope(new(runId, revision, Id, invocation,
                (kind, data) => Emit(kind, Id, invocation, null, data)));
            if (node.McpArgumentsJson is { } configuredArguments)
            {
                using var document = JsonDocument.Parse(configuredArguments, new JsonDocumentOptions { MaxDepth = 32 });
                return await scope.InvokeJsonAsync(node.Tool!.BindingId, document.RootElement.Clone(), cancellationToken);
            }
            return await scope.InvokeAsync(node.Tool!.BindingId, input, cancellationToken);
        }

        private async Task<string> InvokeDirectMcpTool(string input, Guid invocation, List<DataInputEvaluation> evaluations, CancellationToken cancellationToken)
        {
            if (tools is null) throw new InvalidOperationException("No tool broker is configured.");
            var usesPins = WorkflowSchemaVersions.UsesDirectMcpPins(revision.Definition.SchemaVersion);
            var mcpArguments = System.Collections.Immutable.ImmutableArray<WorkflowMcpArgumentDescriptor>.Empty;
            if (usesPins && node.McpInputPorts is not null &&
                !WorkflowNodeDescriptorResolver.TryResolveMcpArguments(node, out mcpArguments))
                throw new InvalidOperationException("The direct MCP node's persisted argument pins do not match its resolved descriptor.");
            JsonObject arguments;
            if (usesPins && node.McpInputPorts is not null && node.McpArgumentsJson is null)
            {
                arguments = new();
            }
            else if (node.McpArgumentsJson is { } configuredArguments)
            {
                try { arguments = JsonNode.Parse(configuredArguments)?.AsObject() ?? throw new InvalidOperationException("Configured MCP arguments must be a JSON object."); }
                catch (JsonException exception) { throw new InvalidOperationException("Configured MCP arguments must be a JSON object.", exception); }
            }
            else
            {
                try { arguments = JsonNode.Parse(input)?.AsObject() ?? throw new InvalidOperationException("A direct MCP node requires a JSON object of tool arguments. For text tools, use {\"text\":\"...\"}."); }
                catch (JsonException exception) { throw new InvalidOperationException("A direct MCP node requires a JSON object of tool arguments. For text tools, use {\"text\":\"...\"}.", exception); }
            }
            if (usesPins && node.McpInputPorts is not null)
            {
                foreach (var argument in mcpArguments)
                {
                    var connection = revision.Definition.Connections.FirstOrDefault(edge => edge.Value.Target.NodeId == Id && edge.Value.Target.PortId == argument.Port.Id);
                    if (connection.Value is null) continue;
                    var evaluation = dataEvaluator!.ResolveInput(Id, argument.Port.Id);
                    evaluations.Add(evaluation);
                    arguments[argument.Argument.Name] = ToJsonNode(evaluation.Result);
                }
            }
            var json = JsonSerializer.SerializeToElement(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await using var scope = tools.OpenScope(new(runId, revision, Id, invocation,
                (kind, data) => Emit(kind, Id, invocation, null, data)));
            return await scope.InvokeJsonAsync(node.Tool!.BindingId, json, cancellationToken);
        }

        private static JsonNode ToJsonNode(ExplicitGraphValue value) => value.DataType switch
        {
            "string" when value.Value is string text => JsonValue.Create(text)!,
            "integer" when value.Value is long integer => JsonValue.Create(integer)!,
            "number" when value.Value is double number && double.IsFinite(number) => JsonValue.Create(number)!,
            "boolean" when value.Value is bool boolean => JsonValue.Create(boolean)!,
            "json" when value.Value is JsonElement json => JsonNode.Parse(json.GetRawText())!,
            _ => throw new InvalidOperationException($"MCP argument data type '{value.DataType}' is not supported.")
        };

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
