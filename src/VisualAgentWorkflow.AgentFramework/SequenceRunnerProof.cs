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
        var fixture = new ProofFixture(coordinator);
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(fixture.Build(),
            new SequenceMessage("start", null, Guid.Empty),
            cancellationToken: cancellationToken);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is WorkflowErrorEvent error)
                throw new InvalidOperationException("The private Sequence runner proof failed.", error.Exception);
        }

        var recorded = coordinator.ToExecutionEvents(runId, revisionId, 1, DateTimeOffset.UtcNow);
        return new SequenceRunnerProofResult(recorded, fixture.DispatchOrder.ToArray(), fixture.CompletionOrder.ToArray());
    }

    private sealed class ProofFixture(SequenceRuntimeCoordinator coordinator)
    {
        private readonly Queue<string> dispatchOrder = new();
        private readonly Queue<string> completionOrder = new();

        internal IReadOnlyCollection<string> DispatchOrder => dispatchOrder.ToArray();
        internal IReadOnlyCollection<string> CompletionOrder => completionOrder.ToArray();

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
                if (message.Kind == "start")
                {
                    await fixture.DispatchNextAsync(coordinator, context, cancellationToken);
                    return new("continue", null, Guid.Empty);
                }

                if (message.Kind != "completed") throw new InvalidOperationException("Unexpected Sequence proof message.");
                coordinator.Complete(message.ActivationId, SequenceTerminalState.Completed);
                if (message.OutputId == "third") return new("terminal", null, Guid.Empty);
                await fixture.DispatchNextAsync(coordinator, context, cancellationToken);
                return new("continue", null, Guid.Empty);
            }
        }

        private sealed class Output(string outputId, ProofFixture fixture)
            : Executor<SequenceMessage, SequenceMessage>($"output:{outputId}", declareCrossRunShareable: true)
        {
            public override async ValueTask<SequenceMessage> HandleAsync(SequenceMessage message, IWorkflowContext context,
                CancellationToken cancellationToken = default)
            {
                fixture.completionOrder.Enqueue(message.OutputId!);
                await Task.Yield();
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
