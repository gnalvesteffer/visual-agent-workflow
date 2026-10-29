namespace VisualAgentWorkflow.Core;

public sealed record WorkflowDraft(WorkflowDefinition Definition, long Generation, Guid? HeadRevisionId, Guid? ActiveRevisionId);
public sealed record PublishedWorkflowRevision(int Version, WorkflowRevision Revision);
public sealed record WorkflowAuditEntry(long Sequence, string WorkflowId, string Actor, DateTimeOffset Timestamp,
    string Kind, long Generation, Guid? RevisionId, string ContentHash);

/// <summary>Definition lifecycle. Hosts own actor identity, authorization, and persistence.</summary>
public interface IWorkflowCatalog
{
    Task<WorkflowDraft?> GetDraftAsync(string workflowId);
    Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync();
    Task<WorkflowDraft> SaveDraftAsync(WorkflowDefinition definition, long expectedGeneration);
    Task<PublishedWorkflowRevision> PublishAsync(string workflowId, long expectedGeneration);
    Task<PublishedWorkflowRevision?> GetRevisionAsync(Guid revisionId);
    Task<IReadOnlyList<PublishedWorkflowRevision>> ListRevisionsAsync(string workflowId);
    Task<WorkflowDraft> RestoreDraftAsync(string workflowId, Guid revisionId, long expectedGeneration);
    Task<WorkflowDraft> ActivateAsync(string workflowId, Guid revisionId, long expectedGeneration);
    Task<IReadOnlyList<WorkflowAuditEntry>> ReadAuditAsync(string workflowId);
}

/// <summary>Optional catalog boundary for explicitly publishing a migration candidate.</summary>
public interface IWorkflowMigrationCatalog
{
    Task<PublishedWorkflowRevision> PublishMigratedAsync(WorkflowRevision revision, long expectedGeneration);
}

/// <summary>Catalog boundary for explicitly promoting a saveable invalid draft into a valid revision.</summary>
public interface IWorkflowDraftPromotionCatalog
{
    Task<PublishedWorkflowRevision> PublishPromotedDraftAsync(WorkflowDraftPromotionResult promotion,
        long expectedGeneration);
}

public interface IPublishedWorkflowClient : IWorkflowClient
{
    Task<RunSnapshot> StartPublishedRunAsync(Guid revisionId, string input);
}

public interface IParameterizedPublishedWorkflowClient
{
    Task<RunSnapshot> StartPublishedRunWithParametersAsync(Guid revisionId, string input,
        IReadOnlyDictionary<string, string>? parameters);
    Task<RunSnapshot> StartPublishedPausedWithParametersAsync(Guid revisionId, string input,
        IReadOnlyDictionary<string, string>? parameters);
}

public sealed class WorkflowConflictException(string workflowId, long expected, long actual)
    : InvalidOperationException($"Workflow '{workflowId}' changed: expected generation {expected}, actual {actual}.")
{
    public long ExpectedGeneration { get; } = expected;
    public long ActualGeneration { get; } = actual;
}
