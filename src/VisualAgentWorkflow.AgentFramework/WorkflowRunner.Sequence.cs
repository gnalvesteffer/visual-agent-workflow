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

        var plan = new SequenceProductionPlan(outputs, terminalOutputId, terminalState);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken,
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
        var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, "sequence", 
            plan.Outputs.Select(output => output.OutputId).ToArray(), connectionIds);
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

        var branches = plan.Outputs.ToDictionary(output => output.OutputNodeId,
            output => (Executor<GraphMessage, GraphMessage>)new StringExecutor(output.OutputNodeId,
                revision.Definition.Nodes[output.OutputNodeId], runId, revision, null, tools, agents, null,
                emit, gate, null, rootRevision, workflowPath, collaborationOptions), StringComparer.Ordinal);
        var source = new SequenceProductionSource("sequence", plan, coordinator, FlushSequenceEvents, emit, gate);
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
            builder.AddEdge<GraphMessage>(branches[output.OutputNodeId], (ExecutorBinding)collector,
                message => message?.SequenceLineage?.OutputId == output.OutputId);
        }
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

    private sealed record SequenceProductionPlan(IReadOnlyList<SequenceProductionOutput> Outputs,
        string? TerminalOutputId, SequenceTerminalState TerminalState);

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
                coordinator.Complete(lineage.OutputActivationId, terminalState);
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
