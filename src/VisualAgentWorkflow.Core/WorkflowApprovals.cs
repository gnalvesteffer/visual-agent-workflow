using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

public sealed record ApprovalConfiguration(string Prompt);

/// <summary>Exact next MCP invocation captured by the trusted executor; approval is not a tool grant.</summary>
public sealed record WorkflowApprovalAction(Guid RunId, Guid RevisionId, string WorkflowHash,
    string ApprovalNodeId, Guid ApprovalInvocationId, string TargetNodeId, Guid TargetInvocationId,
    ToolBindingRequest Tool, string Input, string InputHash)
{
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
            action.RunId != snapshot.Header.RunId || action.RevisionId != snapshot.Header.RevisionId || action.WorkflowHash != snapshot.Header.ContentHash ||
            action.ApprovalNodeId != last.NodeId || action.ApprovalInvocationId != last.InvocationId || action.Input.Length > 16_384 ||
            action.InputHash != WorkflowApprovalAction.HashInput(action.Input) || request.ActionHash != action.ComputeHash()) return null;
        return request;
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
