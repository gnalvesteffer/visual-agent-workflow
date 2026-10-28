using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Trusted adapter state, not an importable workflow or a portable cross-version format.</summary>
public sealed record NativeWorkflowCheckpoint(string SessionId, string CheckpointId, string? ParentCheckpointId, string Payload,
    WorkflowVariableSnapshot? VariableState = null);

/// <summary>Host owns atomic evidence/checkpoint storage and recovery admission. Return true to suspend at the saved boundary.</summary>
public interface IWorkflowCheckpointSink
{
    NativeWorkflowCheckpoint? RestoredCheckpoint { get; }
    ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint);
}

public sealed partial class WorkflowRunner
{
    public const string CheckpointRuntimeVersion = "agent-framework/1.26.0;vaw-adapter/1";

    /// <summary>Opt-in deterministic-only native checkpoint path. Null output means a saved suspension, not completion.</summary>
    public async Task<string?> ExecuteCheckpointedAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowCheckpointSink checkpoints, CancellationToken cancellationToken = default)
    {
        return await ExecuteCheckpointedCoreAsync(runId, revision, input, record, checkpoints, cancellationToken, enforceAdmission: true);
    }

    /// <summary>
    /// Test-only proof seam for native checkpoint serialization. It deliberately bypasses
    /// production admission and must not be used as a recovery API.
    /// </summary>
    internal Task<string?> ExecuteCheckpointedProofAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowCheckpointSink checkpoints, CancellationToken cancellationToken = default) =>
        ExecuteCheckpointedCoreAsync(runId, revision, input, record, checkpoints, cancellationToken, enforceAdmission: false);

    private async Task<string?> ExecuteCheckpointedCoreAsync(Guid runId, WorkflowRevision revision, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowCheckpointSink checkpoints, CancellationToken cancellationToken,
        bool enforceAdmission)
    {
        if (enforceAdmission)
            ValidateCheckpointGraph(revision.Definition);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(input);
        if (runId == Guid.Empty || input.Length > 16_384) throw new ArgumentException("A run identity and bounded input are required.");
        var variableStore = revision.Definition.SchemaVersion is WorkflowSchemaVersions.MutableVariablesGraph or WorkflowSchemaVersions.LoopVariablesGraph
            ? checkpoints.RestoredCheckpoint?.VariableState is { } restoredState
                ? WorkflowVariableStore.FromSnapshot(revision.Definition, restoredState)
                : new WorkflowVariableStore(revision.Definition)
            : null;
        var bridge = new NativeCheckpointBridge(checkpoints, variableStore);
        var manager = CheckpointManager.CreateJson(bridge, new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
        long sequence = 0;
        ValueTask Emit(string kind, string? node = null, Guid? invocation = null, string? connection = null, string? data = null) =>
            record(new(runId, revision.RevisionId, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, kind, node, invocation, connection, data));
        var workflow = BuildWorkflow(runId, revision, input, null, Emit, null, revision, [], variableStore: variableStore);
        await Emit(checkpoints.RestoredCheckpoint is null ? ExecutionEventKinds.Started : ExecutionEventKinds.Recovered);
        try
        {
            await using var run = checkpoints.RestoredCheckpoint is { } restored
                ? await InProcessExecution.ResumeStreamingAsync(workflow, new CheckpointInfo(restored.SessionId, restored.CheckpointId), manager, cancellationToken)
                : await InProcessExecution.RunStreamingAsync(workflow,
                    new GraphMessage(input, null, null, DataType: WorkflowPorts.Outputs(revision.Definition.Nodes[revision.Definition.EntryNodeId]).Single().DataType),
                    manager, sessionId: runId.ToString("D"), cancellationToken: cancellationToken);
            string? output = null;
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bridge.Suspended) return null;
                if (evt is WorkflowErrorEvent or ExecutorFailedEvent) throw new InvalidOperationException("Checkpointed framework execution failed.");
                if (evt is WorkflowOutputEvent result && result.Data is GraphMessage message) output = message.Text;
            }
            if (bridge.Suspended) return null;
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null) throw new InvalidOperationException("Checkpointed workflow produced no output.");
            await Emit(ExecutionEventKinds.Completed, data: output);
            return output;
        }
        catch (Exception error) when (bridge.Suspended && error is not OutOfMemoryException) { return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Emit(ExecutionEventKinds.Cancelled);
            throw;
        }
        catch
        {
            await Emit(ExecutionEventKinds.Failed, data: "Checkpointed execution failed; no automatic recovery was attempted.");
            throw;
        }
    }

    public static void ValidateCheckpointGraph(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        if (definition.Nodes.Values.Any(n => n.Type is not (NodeTypes.Input or NodeTypes.Output or NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.JsonSelectString or NodeTypes.TextTemplate or NodeTypes.StringConstant or NodeTypes.BooleanConstant or NodeTypes.IntegerConstant or NodeTypes.NumberConstant or NodeTypes.VariableGet or NodeTypes.VariableSet)))
            throw new InvalidOperationException("Checkpoint recovery currently permits deterministic graph nodes only; agents, MCP and approvals require a separate recovery policy.");
    }

    private sealed class SavedBoundarySignal : Exception;

    private sealed class NativeCheckpointBridge(IWorkflowCheckpointSink sink, WorkflowVariableStore? variableStore) : ICheckpointStore<JsonElement>
    {
        private NativeWorkflowCheckpoint? latest = sink.RestoredCheckpoint;
        private readonly List<(CheckpointInfo Info, string? Parent)> index = sink.RestoredCheckpoint is { } restored
            ? [(new(restored.SessionId, restored.CheckpointId), restored.ParentCheckpointId)] : [];
        public bool Suspended { get; private set; }

        public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
        {
            var key = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
            var checkpoint = new NativeWorkflowCheckpoint(sessionId, key.CheckpointId, parent?.CheckpointId, value.GetRawText(), variableStore?.Snapshot());
            if (await sink.CaptureAsync(checkpoint))
            {
                // Saving at the native superstep callback precedes scheduling the next superstep.
                Suspended = true;
                throw new SavedBoundarySignal();
            }
            latest = checkpoint;
            index.Add((key, parent?.CheckpointId));
            return key;
        }

        public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
        {
            var checkpoint = latest;
            if (checkpoint is null || checkpoint.CheckpointId != key.CheckpointId || checkpoint.SessionId != sessionId || key.SessionId != sessionId)
                throw new InvalidDataException("Checkpoint identity mismatch.");
            using var document = JsonDocument.Parse(checkpoint.Payload);
            return ValueTask.FromResult(document.RootElement.Clone());
        }

        public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null) =>
            ValueTask.FromResult<IEnumerable<CheckpointInfo>>(index.Where(c => c.Info.SessionId == sessionId &&
                (withParent is null || c.Parent == withParent.CheckpointId)).Select(c => c.Info).ToArray());
    }
}
