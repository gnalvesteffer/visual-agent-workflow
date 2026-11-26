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

/// <summary>Reserved node IDs that the production Sequence runtime records for its own executors.</summary>
internal static class SequenceRuntimeExecutors
{
    public const string CollectorNodeId = "sequence-collector";
    public const string TerminalNodeId = "sequence-terminal";
    public static readonly ImmutableHashSet<string> NodeIds = ImmutableHashSet.Create(StringComparer.Ordinal, CollectorNodeId, TerminalNodeId);
}

/// <summary>Pure projection of recorded facts, never an executor, side-effect detector or permission grant.</summary>
public static class WorkflowWorkEvidence
{
    public const int MaximumReturnedWork = 256;

    public static RunWorkEvidence Read(RunSnapshot snapshot) => Read(snapshot, MaximumReturnedWork);

    /// <summary>Projects every unresolved item for trusted host-side exact-identity checks. Do not use for unbounded public responses.</summary>
    public static RunWorkEvidence ReadAll(RunSnapshot snapshot) => Read(snapshot, int.MaxValue);

    private static RunWorkEvidence Read(RunSnapshot snapshot, int maximumReturnedWork)
    {
        var work = new List<Pending>();
        var root = snapshot.Header.Definition;
        // Child-run events carry the call's scope and child node IDs; they are correlated against the pinned child definition.
        var childScopes = WorkflowCallScopes.Read(snapshot.Events, root, out var incomplete);
        foreach (var evt in snapshot.Events)
        {
            var (owner, definition) = childScopes.Resolve(evt.ScopeId, root);
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
                        if (evt.Kind == ExecutionEventKinds.ToolFailed && IsRecoverableFeedback(evt))
                        {
                            tool.Resolved = true;
                        }
                        else if (tool.Authorized || evt.Kind == ExecutionEventKinds.ToolUncertain)
                        { tool.State = UnresolvedWorkState.OutcomeUnknown; tool.LastSequence = evt.Sequence; tool.LegacyClosed = true; }
                        else tool.Resolved = true;
                    }
                    break;
                case ExecutionEventKinds.SequenceOutputAbandoned:
                    // The abandoned executor may still be running or may already have produced external effects, so the
                    // output is never settled. It reuses the existing node/outcome-unknown shape, correlated by its
                    // activation, so older consumers need no new enum value.
                    if (!SequenceEvidence.TryRead(evt, out var abandoned) || abandoned?.ActivationId is not { } activationId ||
                        evt.NodeId is null || evt.InvocationId is null || !IsRecordedNode(definition, evt.NodeId))
                        incomplete = true;
                    else
                        work.Add(new(UnresolvedWorkKind.Node, owner, evt.NodeId, evt.InvocationId, activationId, evt.Sequence)
                        { State = UnresolvedWorkState.OutcomeUnknown });
                    break;
                // Wait evidence reuses the Node kind, correlated by wait ID, so no UnresolvedWorkKind value is added
                // (HttpWorkflowRunObserver rejects undefined enum values from a newer server).
                case ExecutionEventKinds.WaitRequested:
                    if (Correlation("waitId") is { } requestedWait) Start(UnresolvedWorkKind.Node, requestedWait); else incomplete = true;
                    break;
                case ExecutionEventKinds.WaitElapsed or ExecutionEventKinds.WaitCancelled or ExecutionEventKinds.WaitInterrupted:
                    // A settlement without its wait ID must never resolve the node's own started entry.
                    if (Correlation("waitId") is { } settledWait) Finish(UnresolvedWorkKind.Node, settledWait); else incomplete = true;
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
                    !IsRecordedNode(definition, evt.NodeId) || kind == UnresolvedWorkKind.Approval && callId is null)
                    incomplete = true;
                if (work.Any(w => Matches(w, kind, callId))) incomplete = true;
                work.Add(new(kind, owner, evt.NodeId, evt.InvocationId, callId, evt.Sequence));
            }

            Pending? Find(UnresolvedWorkKind kind, Guid? callId)
            {
                var matches = work.Where(w => Matches(w, kind, callId)).ToArray();
                if (matches.Length != 1 || evt.InvocationId is null) { incomplete = true; return null; }
                return matches[0];
            }

            void Finish(UnresolvedWorkKind kind, Guid? callId)
            {
                if (Find(kind, callId) is { } pending) pending.Resolved = true;
            }

            bool Matches(Pending w, UnresolvedWorkKind kind, Guid? callId) =>
                !w.Resolved && w.Kind == kind && w.Owner == owner && w.NodeId == evt.NodeId && w.InvocationId == evt.InvocationId &&
                (callId is not null ? w.CallId == callId : w.CallId is null && !w.LegacyClosed);
        }
        // Child-run work keeps no node ID: the public item has no scope, so a child node ID would read as a root node.
        var unsettled = work.Where(w => !w.Resolved).Select(w => new UnresolvedRunWork(w.Kind, w.State,
            w.Owner is null && w.NodeId is { } id && root.Nodes.ContainsKey(id) ? id : null,
            w.InvocationId, w.CallId, w.RequestedSequence, w.LastSequence)).ToArray();
        return new(unsettled.Take(maximumReturnedWork).ToImmutableArray(), unsettled.Length, incomplete);
    }

    /// <summary>
    /// A definition node, or a production Sequence runtime executor (collector or terminal) of a definition that has a
    /// Sequence node. Those executors record their own node lifecycle under reserved IDs that no definition declares.
    /// </summary>
    private static bool IsRecordedNode(WorkflowDefinition definition, string nodeId) =>
        definition.Nodes.ContainsKey(nodeId) ||
        SequenceRuntimeExecutors.NodeIds.Contains(nodeId) && definition.Nodes.Values.Any(node => node.Type == NodeTypes.Sequence);

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

    private static bool IsRecoverableFeedback(ExecutionEvent evt)
    {
        if (evt.Data is null) return false;
        try
        {
            using var json = JsonDocument.Parse(evt.Data);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("disposition", out var disposition) &&
                disposition.ValueKind == JsonValueKind.String &&
                string.Equals(disposition.GetString(), ToolInvocationFailureDisposition.RecoverableFeedback.ToString(), StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private sealed class Pending(UnresolvedWorkKind kind, string? owner, string? nodeId, Guid? invocationId, Guid? callId, long sequence)
    {
        public UnresolvedWorkKind Kind { get; } = kind;
        /// <summary>The child-run scope whose definition owns the node, or null for the root workflow.</summary>
        public string? Owner { get; } = owner;
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

    /// <summary>
    /// Child-run scopes proven by <c>subworkflow.evidence</c>: each names a WorkflowCall or WorkflowInterfaceCall node of the
    /// definition that owns the scope's parent and the exact pinned revision and hash it embeds. Unproven scopes stay with the
    /// root definition, so their child node IDs still mark correlation incomplete.
    /// </summary>
    private sealed class WorkflowCallScopes
    {
        private readonly Dictionary<string, ChildPin> calls = new(StringComparer.Ordinal);

        private sealed record ChildPin(Guid RevisionId, string ContentHash, WorkflowDefinition Definition);

        public static WorkflowCallScopes Read(ImmutableArray<ExecutionEvent> events, WorkflowDefinition root, out bool incomplete)
        {
            var scopes = new WorkflowCallScopes();
            incomplete = false;
            foreach (var evt in events.Where(evt => evt.Kind == ExecutionEventKinds.SubworkflowEvidence))
                incomplete |= !scopes.TryAdd(evt, root);
            return scopes;
        }

        /// <summary>Returns the deepest proven child scope containing <paramref name="scopeId"/> and its definition.</summary>
        public (string? Owner, WorkflowDefinition Definition) Resolve(string? scopeId, WorkflowDefinition root)
        {
            // Walks the scope's own prefixes, longest first, so the cost is its depth rather than the number of calls.
            for (var scope = calls.Count == 0 ? null : scopeId; scope is not null;
                scope = scope.LastIndexOf('/') is var slash and > 0 ? scope[..slash] : null)
                if (calls.TryGetValue(scope, out var pin)) return (scope, pin.Definition);
            return (null, root);
        }

        private bool TryAdd(ExecutionEvent evt, WorkflowDefinition root)
        {
            if (evt.ScopeId is not { } scope || !WorkflowCallLifecycleEvidence.TryRead(evt.Data, out var evidence) ||
                evidence is null || evt.NodeId != evidence.CallNodeId ||
                scope != evidence.CallNodeId && !scope.EndsWith("/" + evidence.CallNodeId, StringComparison.Ordinal))
                return false;
            var parentScope = scope.Length == evidence.CallNodeId.Length ? null : scope[..^(evidence.CallNodeId.Length + 1)];
            var (_, parent) = Resolve(parentScope, root);
            if (!parent.Nodes.TryGetValue(evidence.CallNodeId, out var node) || Pin(node) is not { } call ||
                call.RevisionId != evidence.ChildRevisionId ||
                !string.Equals(call.ContentHash, evidence.ChildContentHash, StringComparison.Ordinal))
                return false;
            if (calls.TryGetValue(scope, out var known))
                return known.RevisionId == call.RevisionId && string.Equals(known.ContentHash, call.ContentHash, StringComparison.Ordinal);
            calls.Add(scope, call);
            return true;
        }

        private static ChildPin? Pin(NodeDefinition node) => node switch
        {
            { WorkflowCall: { } call } => new(call.RevisionId, call.ContentHash, call.Definition),
            { WorkflowInterfaceCall: { } call } when call.Implementation?.Definition?.Operations is { } operations &&
                call.OperationId is { } operationId && operations.TryGetValue(operationId, out var pinned) && pinned is not null =>
                new(pinned.RevisionId, pinned.ContentHash, pinned.Definition),
            _ => null
        };
    }
}
