using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RunReconciliationAssertion>))]
public enum RunReconciliationAssertion { BelievedCompleted, BelievedNotApplied, RemainsUnknown }

/// <summary>Exact identity of one recorded unresolved model or tool request.</summary>
public sealed record RunReconciliationWorkIdentity(UnresolvedWorkKind Kind, long RequestedSequence, string NodeId,
    Guid InvocationId, Guid? CallId);

/// <summary>Caller-supplied assertion request. Actor and timestamp are assigned by the trusted host.</summary>
public sealed record RunReconciliationDecisionCommand(Guid DecisionId, Guid RunId, Guid RevisionId, string ContentHash,
    long AssessmentSequence, RunReconciliationWorkIdentity Work, RunReconciliationAssertion Assertion);

/// <summary>Append-only human assertion; it is not evidence of an external effect or permission to retry.</summary>
public sealed record RunReconciliationDecision(Guid DecisionId, Guid RunId, Guid RevisionId, string ContentHash,
    long AssessmentSequence, RunReconciliationWorkIdentity Work, RunReconciliationAssertion Assertion,
    string ActorId, DateTimeOffset RecordedAt);

/// <summary>Optional local persistence capability for human assertions separate from immutable execution events.</summary>
public interface IWorkflowRunReconciliationDecisionStore
{
    IReadOnlyList<RunReconciliationDecision> GetReconciliationDecisions(Guid runId);
    RunReconciliationDecision RecordReconciliationDecision(RunReconciliationDecisionCommand command,
        string actorId, DateTimeOffset recordedAt);
}

/// <summary>Implemented by a host to map its authenticated principal to a stable, non-secret actor identifier.</summary>
public interface IWorkflowRunReconciliationDecisionActor
{
    string GetCurrentActorId();
}

/// <summary>Host-facing command service; it never accepts actor identity from the decision command.</summary>
public sealed class WorkflowRunReconciliationDecisionService(
    IWorkflowRunReconciliationDecisionStore store,
    IWorkflowRunReconciliationDecisionActor actor)
{
    public RunReconciliationDecision Record(RunReconciliationDecisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var actorId = actor.GetCurrentActorId();
        if (string.IsNullOrWhiteSpace(actorId) || actorId.Length > 256)
            throw new UnauthorizedAccessException("The host did not provide a valid authenticated actor identity.");
        return store.RecordReconciliationDecision(command, actorId, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<RunReconciliationDecision> Read(Guid runId) => store.GetReconciliationDecisions(runId);
}

/// <summary>The proposed decision was stale, mismatched, ambiguous, duplicate, or contradicted a previous assertion.</summary>
public sealed class WorkflowRunReconciliationDecisionConflictException(string message) : InvalidOperationException(message);
