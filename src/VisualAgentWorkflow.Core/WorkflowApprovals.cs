using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

public sealed record ApprovalConfiguration(string Prompt);

/// <summary>One immutable workflow-call edge in a nested approval's route from the root graph.</summary>
public sealed record WorkflowApprovalScope(string CallNodeId, Guid RevisionId, string ContentHash);

/// <summary>Exact next MCP invocation captured by the trusted executor; approval is not a tool grant.</summary>
public sealed record WorkflowApprovalAction(Guid RunId, Guid RevisionId, string WorkflowHash,
    string ApprovalNodeId, Guid ApprovalInvocationId, string TargetNodeId, Guid TargetInvocationId,
    ToolBindingRequest Tool, string Input, string InputHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ExecutingRevisionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ExecutingWorkflowHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<WorkflowApprovalScope>? WorkflowPath { get; init; }
    [JsonIgnore] public string? ScopeId => WorkflowPath is { Length: > 0 } path
        ? string.Join("/", path.Select(scope => scope.CallNodeId)) : null;

    public static string HashInput(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public string ComputeHash() => HashInput(JsonSerializer.Serialize(this, JsonSerializerOptions.Web));
}

public sealed record WorkflowApprovalRequest(Guid RequestId, WorkflowApprovalAction Action, string ActionHash, string Prompt, DateTimeOffset RequestedAt);
public sealed record WorkflowApprovalDecision(Guid RequestId, string ActionHash, bool Approved, string Actor);
public sealed record WorkflowApprovalPermit(Guid RequestId, string ActionHash, Guid TargetInvocationId);

/// <summary>Optional trusted execution gate. Cancellation must unblock the wait; no implicit approval.</summary>
public interface IWorkflowApprovalGate
{
    ValueTask<WorkflowApprovalPermit> WaitForApprovalAsync(WorkflowApprovalAction action, string prompt, CancellationToken cancellationToken);
}

/// <summary>Optional control capability. Hosts authorize callers and attribute decisions server-side.</summary>
public interface IWorkflowApprovalClient
{
    Task DecideApprovalAsync(Guid runId, Guid requestId, string actionHash, bool approve);
}

public sealed class WorkflowApprovalConflictException() : InvalidOperationException("The approval is stale, already decided differently, or no longer owned by this runtime.");
public sealed class WorkflowApprovalRejectedException() : InvalidOperationException("The recorded action was rejected.");

public static class WorkflowApprovalEvidence
{
    public static WorkflowApprovalRequest? Pending(RunSnapshot snapshot)
    {
        if (snapshot.IsTerminal) return null;
        var last = snapshot.Events.LastOrDefault(e => IsTransition(e.Kind));
        if (last?.Kind != ExecutionEventKinds.ApprovalRequested) return null;
        var request = ReadRequest(last);
        if (request?.Action is not { Input: not null, Tool: not null } action || request.RequestId == Guid.Empty ||
            action.ApprovalNodeId != last.NodeId || action.ApprovalInvocationId != last.InvocationId ||
            action.ScopeId != last.ScopeId || request.ActionHash != action.ComputeHash() ||
            !MatchesDefinition(snapshot.Header.Definition, snapshot.Header.RunId, snapshot.Header.RevisionId,
                snapshot.Header.ContentHash, action, request.Prompt)) return null;
        return request;
    }

    /// <summary>Checks root identity, every pinned call edge, and the exact approval-to-tool edge.</summary>
    public static bool MatchesDefinition(WorkflowDefinition rootDefinition, Guid runId, Guid rootRevisionId,
        string rootContentHash, WorkflowApprovalAction action, string prompt)
    {
        if (action.RunId != runId || action.RevisionId != rootRevisionId || action.WorkflowHash != rootContentHash ||
            action.ApprovalInvocationId == Guid.Empty || action.TargetInvocationId == Guid.Empty ||
            action.Input is null || action.Input.Length > 16_384 ||
            action.InputHash != WorkflowApprovalAction.HashInput(action.Input)) return false;

        var path = action.WorkflowPath ?? ImmutableArray<WorkflowApprovalScope>.Empty;
        if (path.IsDefault || path.Length >= WorkflowCallLimits.MaximumDepth) return false;
        var definition = rootDefinition;
        var expectedRevisionId = rootRevisionId;
        var expectedContentHash = rootContentHash;
        foreach (var scope in path)
        {
            if (scope is null || string.IsNullOrWhiteSpace(scope.CallNodeId) || scope.CallNodeId.Contains('/') ||
                !definition.Nodes.TryGetValue(scope.CallNodeId, out var callNode) || callNode.Type != NodeTypes.WorkflowCall ||
                callNode.WorkflowCall is not { } call || call.RevisionId != scope.RevisionId ||
                call.ContentHash != scope.ContentHash || call.Definition is not { } childDefinition) return false;
            try
            {
                if (!WorkflowJson.MatchesHash(childDefinition, scope.ContentHash)) return false;
            }
            catch (ArgumentException) { return false; }
            definition = childDefinition;
            expectedRevisionId = call.RevisionId;
            expectedContentHash = call.ContentHash;
        }

        if ((action.ExecutingRevisionId ?? action.RevisionId) != expectedRevisionId ||
            (action.ExecutingWorkflowHash ?? action.WorkflowHash) != expectedContentHash ||
            !definition.Nodes.TryGetValue(action.ApprovalNodeId, out var approvalNode) ||
            approvalNode.Type != NodeTypes.Approval || approvalNode.Approval?.Prompt != prompt ||
            !definition.Nodes.TryGetValue(action.TargetNodeId, out var target) || target.Type != NodeTypes.McpTool ||
            target.Tool != action.Tool ||
            !definition.Connections.Values.Any(edge => edge.Source.NodeId == action.ApprovalNodeId && edge.Target.NodeId == action.TargetNodeId)) return false;

        return true;
    }

    public static bool IsTransition(string kind) => kind is ExecutionEventKinds.ApprovalRequested or ExecutionEventKinds.ApprovalApproved
        or ExecutionEventKinds.ApprovalRejected or ExecutionEventKinds.ApprovalCancelled;

    public static WorkflowApprovalRequest? ReadRequest(ExecutionEvent evt) => Read<WorkflowApprovalRequest>(evt.Data);
    public static WorkflowApprovalDecision? ReadDecision(ExecutionEvent evt) => Read<WorkflowApprovalDecision>(evt.Data);
    private static T? Read<T>(string? data) where T : class
    {
        if (data is null) return null;
        try { return JsonSerializer.Deserialize<T>(data, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }
}
