using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>
/// Outcome of <see cref="WorkflowOperationStartExtensions.StartOperationAsync"/>. <see cref="Run"/> is null exactly
/// when validation rejected the request, in which case nothing was started or persisted. The operation id and actor
/// are returned to the host here; the run store keeps no operation or actor metadata (Decision 0080).
/// </summary>
public sealed record WorkflowOperationStartResult(WorkflowStartRequestResult Validation, RunSnapshot? Run,
    string? OperationId, string? Actor)
{
    public bool Started => Run is not null;
    public ImmutableArray<WorkflowStartRequestError> Errors => Validation.Errors;
}

/// <summary>Host-side shim for Decision 0080's first slice: validate an operation request, then start unchanged.</summary>
public static class WorkflowOperationStartExtensions
{
    public const int MaximumActorLength = 128;

    /// <summary>
    /// Validates <paramref name="request"/> against <paramref name="implementation"/> and, only when valid, calls the
    /// existing <see cref="LocalWorkflowRuntime.StartRevisionAsync"/> with the mapped entry input and the pinned
    /// workflow revision. The host owns authorization; <paramref name="actor"/> is an optional host-supplied label
    /// that is bounded, returned in the result, and never written to events or the run store.
    /// </summary>
    public static async Task<WorkflowOperationStartResult> StartOperationAsync(this LocalWorkflowRuntime runtime,
        WorkflowStartRequest request, WorkflowInterfaceImplementationRevision implementation, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var validation = WorkflowStartRequestValidator.Validate(request, implementation);
        if (actor is not null && (actor.Length is 0 or > MaximumActorLength || actor.Any(char.IsControl)))
            validation = new([.. validation.Errors,
                new("invalid_actor", "actor", $"The actor must be 1–{MaximumActorLength} characters without control characters.")],
                null, validation.Operation);
        if (!validation.Succeeded) return new(validation, null, null, null);

        var pin = validation.Operation!.Workflow;
        var revision = WorkflowRevision.Rehydrate(pin.RevisionId, pin.Definition, "operation-start", null,
            DateTimeOffset.UtcNow, pin.ContentHash);
        var run = await runtime.StartRevisionAsync(revision, validation.EntryInput!);
        return new(validation, run, validation.Operation.OperationId, actor);
    }
}
