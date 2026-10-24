using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RunRecoveryDisposition>))]
public enum RunRecoveryDisposition { Terminal, SavedSuspension, UnresolvedWork, EvidenceAdvanced, CheckpointUnavailable, CheckpointIncompatible, NoSavedSuspension }
[JsonConverter(typeof(JsonStringEnumConverter<UnresolvedWorkKind>))]
public enum UnresolvedWorkKind { Node, Model, Tool, Approval }
[JsonConverter(typeof(JsonStringEnumConverter<UnresolvedWorkState>))]
public enum UnresolvedWorkState { AwaitingOutcome, OutcomeUnknown, AwaitingDecision }

/// <summary>Correlation-only evidence. Contains no input, output, prompts, credentials or native checkpoint state.</summary>
public sealed record UnresolvedRunWork(UnresolvedWorkKind Kind, UnresolvedWorkState State, string? NodeId,
    Guid? InvocationId, Guid? CallId, long RequestedSequence, long LastObservedSequence);
public sealed record RunCheckpointSummary(string CheckpointId, long Sequence, string RuntimeVersion);

/// <summary>Read-only assessment at one evidence cursor. Eligibility is not ownership, authorization or a liveness probe.</summary>
public sealed record RunReconciliation(Guid RunId, Guid RevisionId, string ContentHash, long LastSequence, string Status,
    RunRecoveryDisposition Disposition, bool CanResume, string Reason, RunCheckpointSummary? Checkpoint,
    ImmutableArray<UnresolvedRunWork> UnresolvedWork, int TotalUnresolvedWork, bool HasIncompleteCorrelation);

/// <summary>Optional observer capability. Must enforce the same visibility policy as snapshot reads.</summary>
public interface IWorkflowRunReconciliationObserver
{
    Task<RunReconciliation?> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record RunWorkEvidence(ImmutableArray<UnresolvedRunWork> UnresolvedWork, int TotalUnresolvedWork, bool HasIncompleteCorrelation);

/// <summary>Pure projection of recorded facts, never an executor, side-effect detector or permission grant.</summary>
public static class WorkflowWorkEvidence
{
    public const int MaximumReturnedWork = 256;

    public static RunWorkEvidence Read(RunSnapshot snapshot)
    {
        var work = new List<Pending>();
        var incomplete = false;
        foreach (var evt in snapshot.Events)
        {
            switch (evt.Kind)
            {
                case ExecutionEventKinds.NodeStarted: Start(UnresolvedWorkKind.Node, null); break;
                case ExecutionEventKinds.NodeCompleted or ExecutionEventKinds.NodeFailed or ExecutionEventKinds.NodeCancelled:
                    Finish(UnresolvedWorkKind.Node, null); break;
                case ExecutionEventKinds.ModelRequested: Start(UnresolvedWorkKind.Model, Correlation("modelCallId")); break;
                case ExecutionEventKinds.ModelResponded: Finish(UnresolvedWorkKind.Model, Correlation("modelCallId")); break;
                case ExecutionEventKinds.ToolRequested: Start(UnresolvedWorkKind.Tool, Correlation("toolCallId")); break;
                case ExecutionEventKinds.ToolAuthorized:
                    if (Find(UnresolvedWorkKind.Tool, Correlation("toolCallId")) is { } authorized)
                    { authorized.Authorized = true; authorized.LastSequence = evt.Sequence; }
                    break;
                case ExecutionEventKinds.ToolCompleted or ExecutionEventKinds.ToolDenied:
                    Finish(UnresolvedWorkKind.Tool, Correlation("toolCallId")); break;
                case ExecutionEventKinds.ToolFailed or ExecutionEventKinds.ToolUncertain:
                    if (Find(UnresolvedWorkKind.Tool, Correlation("toolCallId")) is { } tool)
                    {
                        if (tool.Authorized || evt.Kind == ExecutionEventKinds.ToolUncertain)
                        { tool.State = UnresolvedWorkState.OutcomeUnknown; tool.LastSequence = evt.Sequence; tool.LegacyClosed = true; }
                        else tool.Resolved = true;
                    }
                    break;
                case ExecutionEventKinds.ApprovalRequested: Start(UnresolvedWorkKind.Approval, Correlation("requestId")); break;
                case ExecutionEventKinds.ApprovalApproved or ExecutionEventKinds.ApprovalRejected or ExecutionEventKinds.ApprovalCancelled:
                    Finish(UnresolvedWorkKind.Approval, Correlation("requestId")); break;
            }

            work.RemoveAll(w => w.Resolved);

            Guid? Correlation(string property)
            {
                var id = ReadId(evt, property, out var malformed);
                incomplete |= malformed;
                return id;
            }

            void Start(UnresolvedWorkKind kind, Guid? callId)
            {
                if (evt.InvocationId is null || evt.InvocationId == Guid.Empty || evt.NodeId is null ||
                    !snapshot.Header.Definition.Nodes.ContainsKey(evt.NodeId) || kind == UnresolvedWorkKind.Approval && callId is null)
                    incomplete = true;
                if (work.Any(w => !w.Resolved && w.Kind == kind && w.NodeId == evt.NodeId && w.InvocationId == evt.InvocationId &&
                    (callId is not null ? w.CallId == callId : w.CallId is null && !w.LegacyClosed))) incomplete = true;
                work.Add(new(kind, evt.NodeId, evt.InvocationId, callId, evt.Sequence));
            }

            Pending? Find(UnresolvedWorkKind kind, Guid? callId)
            {
                var matches = work.Where(w => !w.Resolved && w.Kind == kind && w.NodeId == evt.NodeId && w.InvocationId == evt.InvocationId &&
                    (callId is not null ? w.CallId == callId : w.CallId is null && !w.LegacyClosed)).ToArray();
                if (matches.Length != 1 || evt.InvocationId is null) { incomplete = true; return null; }
                return matches[0];
            }

            void Finish(UnresolvedWorkKind kind, Guid? callId)
            {
                if (Find(kind, callId) is { } pending) pending.Resolved = true;
            }
        }
        var unsettled = work.Where(w => !w.Resolved).Select(w => new UnresolvedRunWork(w.Kind, w.State, w.NodeId is { } id && snapshot.Header.Definition.Nodes.ContainsKey(id) ? id : null,
            w.InvocationId, w.CallId, w.RequestedSequence, w.LastSequence)).ToArray();
        return new(unsettled.Take(MaximumReturnedWork).ToImmutableArray(), unsettled.Length, incomplete);
    }

    private static Guid? ReadId(ExecutionEvent evt, string property, out bool malformed)
    {
        malformed = true;
        if (evt.Data is null) return null;
        try
        {
            using var json = JsonDocument.Parse(evt.Data);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!json.RootElement.TryGetProperty(property, out var value)) { malformed = false; return null; }
            if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty)
            { malformed = false; return id; }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class Pending(UnresolvedWorkKind kind, string? nodeId, Guid? invocationId, Guid? callId, long sequence)
    {
        public UnresolvedWorkKind Kind { get; } = kind;
        public string? NodeId { get; } = nodeId;
        public Guid? InvocationId { get; } = invocationId;
        public Guid? CallId { get; } = callId;
        public long RequestedSequence { get; } = sequence;
        public long LastSequence { get; set; } = sequence;
        public bool Authorized { get; set; }
        public bool Resolved { get; set; }
        public bool LegacyClosed { get; set; }
        public UnresolvedWorkState State { get; set; } = kind == UnresolvedWorkKind.Approval ? UnresolvedWorkState.AwaitingDecision : UnresolvedWorkState.AwaitingOutcome;
    }
}
