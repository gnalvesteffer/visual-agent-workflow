using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>
/// Private proof harness for the deferred Sequence contract. It deliberately uses
/// the pinned Agent Framework streaming runner, but is not a workflow node or a
/// public schema feature.
/// </summary>
internal static class SequenceRunnerProof
{
    internal static async Task<SequenceRunnerProofResult> ExecuteAsync(Guid runId, Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        var outputIds = new[] { "first", "second", "third" };
        var connectionIds = outputIds.ToDictionary(id => id, id => $"sequence-proof:{id}", StringComparer.Ordinal);
        var sequenceInvocationId = Guid.NewGuid();
        var coordinator = new SequenceRuntimeCoordinator(sequenceInvocationId, "sequence-proof", outputIds, connectionIds);
        var fixture = new ProofFixture(runId, revisionId, coordinator);
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(fixture.Build(),
            new SequenceMessage("start", null, Guid.Empty),
            cancellationToken: cancellationToken);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is WorkflowErrorEvent error)
                throw new InvalidOperationException("The private Sequence runner proof failed.", error.Exception);
        }

        fixture.ThrowIfActive();
        var recorded = coordinator.ToExecutionEvents(runId, revisionId, fixture.RecordedEvents.Count + 1, DateTimeOffset.UtcNow);
        return new SequenceRunnerProofResult(fixture.RecordedEvents.Concat(recorded).ToArray(),
            fixture.DispatchOrder.ToArray(), fixture.CompletionOrder.ToArray());
    }

    private sealed class ProofFixture(Guid runId, Guid revisionId, SequenceRuntimeCoordinator coordinator)
    {
        private readonly Queue<string> dispatchOrder = new();
        private readonly Queue<string> completionOrder = new();
        private readonly ExecutionActivationLedger activationLedger = new();
        private readonly List<ExecutionEvent> recordedEvents = [];
        private long sequence;

        internal IReadOnlyCollection<string> DispatchOrder => dispatchOrder.ToArray();
        internal IReadOnlyCollection<string> CompletionOrder => completionOrder.ToArray();
        internal IReadOnlyList<ExecutionEvent> RecordedEvents
        {
            get { lock (recordedEvents) return recordedEvents.OrderBy(item => item.Sequence).ToArray(); }
        }

        internal void Begin(string nodeId, Guid invocationId, string? data = null) => Record(ExecutionEventKinds.NodeStarted,
            nodeId, invocationId, data);

        internal void Complete(string nodeId, Guid invocationId, string? data = null) => Record(ExecutionEventKinds.NodeCompleted,
            nodeId, invocationId, data);

        internal void ThrowIfActive() => activationLedger.ThrowIfActive();

        private void Record(string kind, string nodeId, Guid invocationId, string? data)
        {
            var executionEvent = new ExecutionEvent(runId, revisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                kind, nodeId, invocationId, null, data);
            activationLedger.Observe(executionEvent);
            lock (recordedEvents) recordedEvents.Add(executionEvent);
        }

        internal Workflow Build()
        {
            var dispatcher = new Dispatcher(this, coordinator);
            var outputs = new Dictionary<string, Output>(StringComparer.Ordinal);
            foreach (var outputId in new[] { "first", "second", "third" })
                outputs[outputId] = new(outputId, this);

            var sink = new Sink();
            var terminal = new Terminal();
            var builder = new WorkflowBuilder(dispatcher);
            foreach (var outputId in outputs.Keys)
            {
                var output = outputs[outputId];
                builder.AddEdge<SequenceMessage>((ExecutorBinding)dispatcher, output,
                    message => message?.Kind == "dispatch" && message.OutputId == outputId);
                builder.AddEdge<SequenceMessage>((ExecutorBinding)output, (ExecutorBinding)dispatcher,
                    message => message?.Kind == "completed");
            }
            builder.AddEdge<SequenceMessage>((ExecutorBinding)dispatcher, (ExecutorBinding)sink,
                message => message?.Kind == "continue");
            builder.AddEdge<SequenceMessage>((ExecutorBinding)dispatcher, (ExecutorBinding)terminal,
                message => message?.Kind == "terminal");
            builder.WithOutputFrom(terminal);
            return builder.Build();
        }

        private sealed class Dispatcher(ProofFixture fixture, SequenceRuntimeCoordinator coordinator)
            : Executor<SequenceMessage, SequenceMessage>("sequence-proof", declareCrossRunShareable: true)
        {
            public override async ValueTask<SequenceMessage> HandleAsync(SequenceMessage message, IWorkflowContext context,
                CancellationToken cancellationToken = default)
            {
                var invocationId = message.ActivationId == Guid.Empty ? Guid.NewGuid() : message.ActivationId;
                fixture.Begin(Id, invocationId, message.Kind);
                if (message.Kind == "start")
                {
                    await fixture.DispatchNextAsync(coordinator, context, cancellationToken);
                    fixture.Complete(Id, invocationId, "waiting-for-first-output");
                    return new("continue", null, Guid.Empty);
                }

                if (message.Kind != "completed") throw new InvalidOperationException("Unexpected Sequence proof message.");
                coordinator.Complete(message.ActivationId, SequenceTerminalState.Completed);
                if (message.OutputId == "third")
                {
                    fixture.Complete(Id, invocationId, "sequence-complete");
                    return new("terminal", null, Guid.Empty);
                }
                await fixture.DispatchNextAsync(coordinator, context, cancellationToken);
                fixture.Complete(Id, invocationId, "waiting-for-next-output");
                return new("continue", null, Guid.Empty);
            }
        }

        private sealed class Output(string outputId, ProofFixture fixture)
            : Executor<SequenceMessage, SequenceMessage>($"output:{outputId}", declareCrossRunShareable: true)
        {
            public override async ValueTask<SequenceMessage> HandleAsync(SequenceMessage message, IWorkflowContext context,
                CancellationToken cancellationToken = default)
            {
                fixture.Begin(Id, message.ActivationId, message.OutputId);
                fixture.completionOrder.Enqueue(message.OutputId!);
                await Task.Yield();
                fixture.Complete(Id, message.ActivationId, message.OutputId);
                return new("completed", message.OutputId, message.ActivationId);
            }
        }

        private sealed class Sink() : Executor<SequenceMessage, SequenceMessage>("sequence-proof-sink", declareCrossRunShareable: true)
        {
            public override ValueTask<SequenceMessage> HandleAsync(SequenceMessage message, IWorkflowContext context,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(message);
        }

        private sealed class Terminal() : Executor<SequenceMessage, string>("sequence-proof-terminal", declareCrossRunShareable: true)
        {
            public override ValueTask<string> HandleAsync(SequenceMessage message, IWorkflowContext context,
                CancellationToken cancellationToken = default) => ValueTask.FromResult("sequence-proof-complete");
        }

        private async Task DispatchNextAsync(SequenceRuntimeCoordinator coordinator, IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            var activationId = Guid.NewGuid();
            var activation = coordinator.DispatchNext(activationId);
            dispatchOrder.Enqueue(activation.OutputId);
            await context.SendMessageAsync(new SequenceMessage("dispatch", activation.OutputId, activation.ActivationId!.Value),
                $"output:{activation.OutputId}", cancellationToken);
        }
    }

    private sealed record SequenceMessage(string Kind, string? OutputId, Guid ActivationId);
}

internal sealed record SequenceRunnerProofResult(
    IReadOnlyList<ExecutionEvent> Events,
    IReadOnlyList<string> DispatchOrder,
    IReadOnlyList<string> CompletionOrder);
