using System.Collections.Immutable;
using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed partial class WorkflowRunner
{
    /// <summary>
    /// Runs a private Sequence-shaped plan through the production runner boundary.
    /// This is intentionally internal and cannot be created from persisted workflow content.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, string? terminalOutputId = null,
        SequenceTerminalState terminalState = SequenceTerminalState.Completed,
        IWorkflowExecutionGate? gate = null, CancellationToken cancellationToken = default)
    {
        var revision = SequenceProofRevision();
        var outputs = new[]
        {
            new SequenceProductionOutput("first", 0, "first", "sequence", "out", "sequence-proof:first"),
            new SequenceProductionOutput("second", 1, "second", "first", "out", "sequence-proof:second"),
            new SequenceProductionOutput("third", 2, "third", "second", "out", "sequence-proof:third")
        };
        if (terminalOutputId is not null && outputs.All(output => output.OutputId != terminalOutputId))
            throw new ArgumentException("The proof terminal output must be one of the declared outputs.", nameof(terminalOutputId));

        var plan = new SequenceProductionPlan("sequence", outputs, terminalOutputId, terminalState);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    /// <summary>
    /// Runs the non-public Sequence-to-Agent lifecycle proof. This deliberately uses a
    /// validator-valid private definition and an injected host Agent executor; it is not
    /// reachable from persisted workflow schemas or Studio authoring.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceAgentProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate? gate = null,
        CancellationToken cancellationToken = default)
    {
        var revision = SequenceAgentProofRevision();
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("agent", 0, "agent", "sequence", "then", "sequence-proof:agent")
        ], null, SequenceTerminalState.Completed, AllowSingleOutput: true);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    /// <summary>Runs the private two-output Agent fan-out proof used to verify sibling settlement.</summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceAgentFanoutProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default)
    {
        var revision = SequenceAgentFanoutProofRevision();
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("failing", 0, "agent-first", "anchor_first", "then", "sequence-proof:failing"),
            new SequenceProductionOutput("sibling", 1, "agent-second", "anchor_second", "then", "sequence-proof:sibling")
        ], null, SequenceTerminalState.Completed, DirectOutputCollection: true, BypassRevisionExecutionEdges: true);
        return ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    private Workflow BuildSequenceProductionWorkflow(Guid runId, WorkflowRevision revision,
        SequenceProductionPlan plan,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit,
        IWorkflowExecutionGate? gate, WorkflowRevision rootRevision,
        ImmutableArray<WorkflowApprovalScope> workflowPath)
    {
        var sequenceInvocationId = Guid.NewGuid();
        var connectionIds = plan.Outputs.ToDictionary(output => output.OutputId, output => output.ConnectionId,
            StringComparer.Ordinal);
        var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, plan.SequenceNodeId,
            plan.Outputs.Select(output => output.OutputId).ToArray(), connectionIds, plan.AllowSingleOutput);
        var sequenceEventGate = new SemaphoreSlim(1, 1);

        async ValueTask FlushSequenceEvents()
        {
            await sequenceEventGate.WaitAsync();
            try
            {
                foreach (var runtimeEvent in coordinator.DrainEvents())
                {
                    var evidence = new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId,
                        runtimeEvent.SequenceNodeId, runtimeEvent.OutputId, runtimeEvent.Ordinal,
                        runtimeEvent.ActivationId, runtimeEvent.ConnectionId);
                    await emit(runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                        runtimeEvent.ConnectionId, SequenceEvidence.Serialize(evidence), null);
                }
            }
            finally
            {
                sequenceEventGate.Release();
            }
        }

        var branchNodeIds = revision.Definition.ExecutionConnections is null
            ? plan.Outputs.Select(output => output.OutputNodeId).Distinct(StringComparer.Ordinal)
            : revision.Definition.Nodes.Where(entry => entry.Value.Type is not (NodeTypes.Input or NodeTypes.Sequence))
                .Select(entry => entry.Key);
        var branches = branchNodeIds
            .ToDictionary(outputNodeId => outputNodeId,
            outputNodeId => (Executor<GraphMessage, GraphMessage>)new StringExecutor(outputNodeId,
                revision.Definition.Nodes[outputNodeId], runId, revision, null, tools, agents, null, null,
                emit, gate, null, rootRevision, workflowPath, collaborationOptions, null,
                async (lineage, terminalState) =>
                {
                    if (coordinator.IsActivationTerminal(lineage.OutputActivationId)) return;
                    coordinator.Complete(lineage.OutputActivationId, terminalState);
                    await FlushSequenceEvents();
                }), StringComparer.Ordinal);
        var source = new SequenceProductionSource(plan.SequenceNodeId, plan, coordinator, FlushSequenceEvents, emit, gate);
        var collector = new SequenceProductionCollector("sequence-collector", plan, coordinator,
            FlushSequenceEvents, emit);
        var sink = new SequenceProductionSink();
        var terminal = new SequenceProductionTerminal("sequence-terminal", emit);
        var builder = new WorkflowBuilder(source);

        foreach (var output in plan.Outputs)
        {
            builder.AddEdge<GraphMessage>((ExecutorBinding)source, branches[output.OutputNodeId],
                message => message?.SequenceLineage?.OutputId == output.OutputId &&
                    message.SourceNode == output.SourceNodeId && message.SourcePort == output.SourcePortId);
            if (revision.Definition.ExecutionConnections is null)
                builder.AddEdge<GraphMessage>(branches[output.OutputNodeId], (ExecutorBinding)collector,
                    message => message?.SequenceLineage?.OutputId == output.OutputId);
        }
        var revisionExecutionEdges = plan.BypassRevisionExecutionEdges
            ? Enumerable.Empty<ConnectionDefinition>()
            : (revision.Definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty).Values;
        foreach (var edge in revisionExecutionEdges.Where(edge =>
                     edge.Source.NodeId != plan.SequenceNodeId && edge.Target.NodeId != plan.SequenceNodeId &&
                     edge.Source.NodeId != revision.Definition.EntryNodeId))
        {
            builder.AddEdge<GraphMessage>(branches[edge.Source.NodeId], branches[edge.Target.NodeId],
                message => message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId);
        }
        foreach (var outputNodeId in plan.BypassRevisionExecutionEdges ? [] : revision.Definition.ExecutionConnections is null
                     ? []
                     : revision.Definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.Output).Select(entry => entry.Key))
        {
            builder.AddEdge<GraphMessage>(branches[outputNodeId], (ExecutorBinding)collector,
                message => message?.SourceNode == outputNodeId && message.SourcePort == "out");
        }
        if (plan.DirectOutputCollection)
            foreach (var output in plan.Outputs)
                builder.AddEdge<GraphMessage>(branches[output.OutputNodeId], (ExecutorBinding)collector,
                    message => message?.SequenceLineage?.OutputId == output.OutputId);
        builder.AddEdge<GraphMessage>((ExecutorBinding)source, (ExecutorBinding)sink,
            message => message?.SourcePort == "continue");
        builder.AddEdge<GraphMessage>((ExecutorBinding)source, (ExecutorBinding)terminal,
            message => message?.SourcePort == "terminal");
        builder.AddEdge<GraphMessage>((ExecutorBinding)collector, (ExecutorBinding)source,
            message => message?.SourcePort == "completed");
        builder.WithOutputFrom(terminal);
        return builder.Build();
    }

    private static WorkflowRevision SequenceProofRevision()
    {
        var definition = new WorkflowDefinition("1.0", "sequence-production-proof", "sequence",
            new Dictionary<string, NodeDefinition>
            {
                ["sequence"] = new(NodeTypes.Input, "1.0.0"),
                ["first"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["second"] = new(NodeTypes.Reverse, "1.0.0"),
                ["third"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("sequence", "out"), new("first", "in"), "data"),
                ["b"] = new(new("first", "out"), new("second", "in"), "data"),
                ["c"] = new(new("second", "out"), new("third", "in"), "data"),
                ["d"] = new(new("third", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        return WorkflowRevision.Create(definition, "private-sequence-production-proof");
    }

    private static WorkflowRevision SequenceAgentProofRevision()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the input unchanged.", []
        ) { MaxTurns = 1 });
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.AgentExecutionRoutes, "sequence-agent-production-proof", "sequence",
            new Dictionary<string, NodeDefinition>
            {
                ["sequence"] = new(NodeTypes.Input, "1.0.0"),
                ["agent"] = agent,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["agent-output"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["sequence-agent"] = new(new("sequence", "then"), new("agent", "execute"), "execution"),
                ["agent-output"] = new(new("agent", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        return WorkflowRevision.Create(definition, "private-sequence-agent-production-proof");
    }

    private static WorkflowRevision SequenceAgentFanoutProofRevision()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("branch_first", "First authored path", "anchor_first"),
            new ParallelBranchDescriptor("branch_second", "Second authored path", "anchor_second"));
        var firstAgent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the first authored response.", []) { MaxTurns = 1 });
        var secondAgent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the second authored response.", []) { MaxTurns = 1 });
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor_first", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "branch_first") })
            .Add("anchor_second", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "branch_second") })
            .Add("agent-first", firstAgent)
            .Add("agent-second", secondAgent)
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branches.Select(branch => branch.Id).ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", new(new("input", "then"), new("fork", "execute"), "execution"))
            .Add("fork-first", new(new("fork", ParallelBranchLimits.PinId("branch_first")), new("anchor_first", "execute"), "execution"))
            .Add("fork-second", new(new("fork", ParallelBranchLimits.PinId("branch_second")), new("anchor_second", "execute"), "execution"))
            .Add("first-agent", new(new("anchor_first", "then"), new("agent-first", "execute"), "execution"))
            .Add("second-agent", new(new("anchor_second", "then"), new("agent-second", "execute"), "execution"))
            .Add("first-join", new(new("agent-first", "out"), new("join", ParallelBranchLimits.PinId("branch_first")), "execution"))
            .Add("second-join", new(new("agent-second", "out"), new("join", ParallelBranchLimits.PinId("branch_second")), "execution"))
            .Add("join-output", new(new("join", "then"), new("output", "execute"), "execution"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-data", new(new("join", "out"), new("output", "in"), "data"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.ConfigurableParallelGraph,
            "sequence-agent-fanout-production-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
        return WorkflowRevision.Create(definition, "private-sequence-agent-fanout-production-proof");
    }

    private SequenceProductionPlan? CreateSequenceProductionPlan(WorkflowRevision revision)
    {
        if (revision.Definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph)) return null;
        var sequence = revision.Definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Sequence);
        if (sequence.Value?.Sequence is not { } configuration || revision.Definition.ExecutionConnections is not { } edges)
            throw new InvalidOperationException("The Sequence definition is missing its ordered outputs or execution connections.");
        var outputs = configuration.Outputs.Select((output, ordinal) =>
        {
            var edge = edges.Single(edge => edge.Value.Source.NodeId == sequence.Key && edge.Value.Source.PortId == SequenceOutputLimits.PinId(output.Id));
            return new SequenceProductionOutput(output.Id, ordinal, edge.Value.Target.NodeId, sequence.Key,
                edge.Value.Source.PortId, edge.Key);
        }).ToArray();
        return new SequenceProductionPlan(sequence.Key, outputs, null, SequenceTerminalState.Completed);
    }

    private sealed record SequenceProductionPlan(string SequenceNodeId, IReadOnlyList<SequenceProductionOutput> Outputs,
        string? TerminalOutputId, SequenceTerminalState TerminalState, bool AllowSingleOutput = false,
        bool DirectOutputCollection = false, bool BypassRevisionExecutionEdges = false);

    private sealed record SequenceProductionOutput(string OutputId, int Ordinal, string OutputNodeId,
        string SourceNodeId, string SourcePortId, string ConnectionId);

    private sealed class SequenceProductionSource(string id, SequenceProductionPlan plan,
        SequenceRuntimeCoordinator coordinator, Func<ValueTask> flushSequenceEvents,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var invocation = Guid.NewGuid();
            await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message.Text, null);
            try
            {
                if (message.SourcePort is "completed")
                {
                    if (message.SequenceLineage is not { } completedLineage)
                        throw new InvalidOperationException("A Sequence completion is missing its lineage.");
                    await flushSequenceEvents();
                    var terminal = coordinator.IsTerminal;
                    await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null,
                        terminal ? "All Sequence outputs completed." : "Waiting for remaining Sequence outputs.",
                        null);
                    return message with
                    {
                        Text = terminal ? coordinator.DeterministicResult ?? message.Text : message.Text,
                        SourceNode = Id,
                        SourcePort = terminal ? "terminal" : "continue",
                        FailureSignal = terminal && coordinator.TerminalState is not null
                    };
                }

                await flushSequenceEvents();
                foreach (var output in plan.Outputs)
                {
                    if (gate is not null)
                        await gate.WaitForBoundaryAsync($"sequence:{output.OutputId}", cancellationToken);
                    var activationId = Guid.NewGuid();
                    var activation = coordinator.DispatchNext(activationId);
                    var lineage = new SequenceMessageLineage(coordinator.SequenceInvocationId,
                        coordinator.SequenceNodeId, activation.OutputId, activation.Ordinal,
                        activation.ActivationId!.Value, output.ConnectionId,
                        $"sequence/{activation.OutputId}");
                    await flushSequenceEvents();
                    await context.SendMessageAsync(new GraphMessage(message.Text, output.SourceNodeId,
                        output.SourcePortId, DataType: "string", SequenceLineage: lineage), output.OutputNodeId,
                        cancellationToken);
                }
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, "All Sequence outputs dispatched.", null);
                return new(message.Text, Id, "continue");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null,
                    "Sequence dispatch cancelled.", null);
                throw;
            }
            catch
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, null,
                    "Sequence dispatch failed.", null);
                throw;
            }
        }
    }

    private sealed class SequenceProductionCollector(string id, SequenceProductionPlan plan,
        SequenceRuntimeCoordinator coordinator, Func<ValueTask> flushSequenceEvents,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            if (message.SequenceLineage is not { } lineage)
                throw new InvalidOperationException("A Sequence descendant completion is missing its lineage.");
            var invocation = Guid.NewGuid();
            await emit(ExecutionEventKinds.NodeStarted, Id, invocation, lineage.ConnectionId,
                message.Text, lineage.DescendantScopeId);
            try
            {
                var terminalState = plan.TerminalOutputId == lineage.OutputId
                    ? plan.TerminalState : SequenceTerminalState.Completed;
                coordinator.Complete(lineage.OutputActivationId, terminalState, terminalState == SequenceTerminalState.Completed ? message.Text : null);
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, lineage.ConnectionId,
                    terminalState == SequenceTerminalState.Completed ? "Sequence output completed." :
                    $"Sequence output ended with {terminalState}.", lineage.DescendantScopeId);
                return message with { SourceNode = Id, SourcePort = "completed" };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, lineage.ConnectionId,
                    "Sequence output cancelled.", lineage.DescendantScopeId);
                throw;
            }
            catch
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, lineage.ConnectionId,
                    "Sequence output failed.", lineage.DescendantScopeId);
                throw;
            }
        }
    }

    private sealed class SequenceProductionSink()
        : Executor<GraphMessage, GraphMessage>("sequence-sink", declareCrossRunShareable: true)
    {
        public override ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(message);
    }

    private sealed class SequenceProductionTerminal(string id,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var invocation = Guid.NewGuid();
            await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message.Text, null);
            await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, message.Text, null);
            return message with { SourceNode = Id, SourcePort = "out" };
        }
    }
}
