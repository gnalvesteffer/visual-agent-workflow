using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Translates the supported string pipeline into actual Agent Framework executors and edges.</summary>
public sealed class PipelineRunner(INodeToolBroker? tools = null)
{
    public async Task<string> ExecuteAsync(
        Guid runId,
        WorkflowRevision revision,
        string input,
        Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(record);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        WorkflowValidator.ThrowIfInvalid(revision.Definition);
        long sequence = 0;
        ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null, string? data = null) =>
            record(new(runId, revision.RevisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow,
                kind, node, invocation, connection, data));

        var definition = revision.Definition;
        var incoming = definition.Connections.ToDictionary(e => e.Value.Target.NodeId, e => e.Key);
        var executors = definition.Nodes.ToDictionary(
            n => n.Key,
            n => new StringExecutor(n.Key, n.Value, runId, revision, tools, incoming.GetValueOrDefault(n.Key), Emit));
        var builder = new WorkflowBuilder(executors[definition.EntryNodeId]);
        foreach (var edge in definition.Connections.Values)
            builder.AddEdge(executors[edge.Source.NodeId], executors[edge.Target.NodeId]);
        var outputId = definition.Nodes.Single(n => n.Value.Type == NodeTypes.Output).Key;
        builder.WithOutputFrom(executors[outputId]);
        var workflow = builder.Build();

        await Emit(ExecutionEventKinds.Started);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: cancellationToken);
            string? output = null;
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                if (evt is WorkflowErrorEvent error)
                    throw new InvalidOperationException("Agent Framework execution failed.", error.Exception);
                if (evt is ExecutorFailedEvent failed)
                    throw new InvalidOperationException($"Executor {failed.ExecutorId} failed.", failed.Data);
                if (evt is WorkflowOutputEvent result && result.Data is string text)
                    output = text;
            }
            if (output is null) throw new InvalidOperationException("The workflow produced no output.");
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

    private sealed class StringExecutor(
        string id,
        NodeDefinition node,
        Guid runId,
        WorkflowRevision revision,
        INodeToolBroker? tools,
        string? incomingConnection,
        Func<string, string?, Guid?, string?, string?, ValueTask> emit) : Executor<string, string>(id)
    {
        public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = Guid.NewGuid();
            // Receiving the downstream message is direct evidence of delivery, not inferred completion.
            if (incomingConnection is not null)
                await emit(ExecutionEventKinds.Transfer, Id, invocation, incomingConnection, message);
            await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message);
            var result = node.Type switch
            {
                NodeTypes.Input or NodeTypes.Output => message,
                NodeTypes.Uppercase => message.ToUpperInvariant(),
                NodeTypes.Reverse => string.Concat(message.EnumerateRunes().Reverse()),
                NodeTypes.McpTool => await InvokeTool(message, invocation, cancellationToken),
                _ => throw new InvalidOperationException("Unsupported executor type.")
            };
            await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, result);
            return result;
        }

        private async Task<string> InvokeTool(string input, Guid invocation, CancellationToken cancellationToken)
        {
            if (tools is null) throw new InvalidOperationException("No tool broker is configured.");
            await using var scope = tools.OpenScope(new(runId, revision, Id, invocation,
                (kind, data) => emit(kind, Id, invocation, null, data)));
            return await scope.InvokeAsync(node.Tool!.BindingId, input, cancellationToken);
        }
    }
}
