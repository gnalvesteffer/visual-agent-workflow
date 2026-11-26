using System.Collections.Immutable;
using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

public sealed partial class WorkflowRunner
{
    /// <summary>
    /// Test-only seam (decision 0070, "Prerequisite proofs: forEachItems isolation and nested-scope playback"). Builds
    /// ONE workflow, so every node has exactly one executor instance, and starts one streaming run per frame
    /// concurrently on that same <see cref="Workflow"/>. Production creates a fresh executor set per run, so no
    /// production path activates one For Each executor under two parent frames at once; this seam is the only way
    /// to exercise the per-activation <c>forEachItems</c> key on a shared instance. Each frame's Item is the run input
    /// for that activation (an Input node reads the active frame's item). The probe reports one executor's live
    /// <c>forEachItems</c> entry count.
    /// </summary>
    internal async Task<IReadOnlyList<string?>> ExecuteSharedExecutorConcurrentActivationsProofAsync(Guid runId,
        WorkflowRevision revision, IReadOnlyList<StructuredActivationFrame> frames,
        Func<ExecutionEvent, ValueTask> record, Action<Func<string, int>>? forEachItemsProbe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(record);
        var runBudget = new WorkflowRunBudget(null, null);
        var ledger = new ExecutionActivationLedger();
        using var frameOwner = new StructuredActivationFrameOwner(null, null, revision.Definition, runBudget: runBudget,
            countLoopEvidenceEvents: true);
        frameOwner.BindRootEventRecorder(runId, revision.RevisionId, record, ledger, clock);
        var variableStore = frameOwner.CreateVariableStore(revision.Definition);
        ValueTask Emit(string kind, string? node, Guid? invocation, string? connection, string? data, string? scopeId) =>
            frameOwner.RecordEventAsync(kind, node, invocation, connection, data, scopeId);

        var workflow = BuildWorkflow(runId, revision, string.Empty,
            WorkflowParameters.Resolve(revision.Definition, null), Emit, null, revision, [],
            variableStore: variableStore, activationFrameOwner: frameOwner,
            observeExecutors: executors => forEachItemsProbe?.Invoke(nodeId =>
                executors[nodeId] is StringExecutor executor ? executor.ActiveForEachItemCount : -1));
        var inputType = WorkflowPorts.Outputs(revision.Definition.Nodes[revision.Definition.EntryNodeId]).Single().DataType;

        async Task<string?> RunOneAsync(StructuredActivationFrame frame)
        {
            await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow,
                new GraphMessage(frame.Item, null, null, DataType: inputType, ActivationFrame: frame),
                cancellationToken: cancellationToken);
            string? output = null;
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                if (evt is WorkflowErrorEvent error)
                    throw new InvalidOperationException("Agent Framework execution failed.", error.Exception);
                if (evt is ExecutorFailedEvent failed)
                    throw new InvalidOperationException($"Executor {failed.ExecutorId} failed.", failed.Data);
                if (evt is WorkflowOutputEvent { Data: GraphMessage message }) output = message.Text;
            }
            return output;
        }

        return await Task.WhenAll(frames.Select(RunOneAsync));
    }
}
