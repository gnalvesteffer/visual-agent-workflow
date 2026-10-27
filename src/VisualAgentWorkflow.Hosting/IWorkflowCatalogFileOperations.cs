using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Optional reversible file-style operations supported by local workflow catalogs.</summary>
public interface IWorkflowCatalogFileOperations
{
    Task<IReadOnlyList<WorkflowDraft>> ListArchivedDraftsAsync();
    Task ArchiveWorkflowAsync(string workflowId, long expectedGeneration);
    Task RestoreArchivedWorkflowAsync(string workflowId, long expectedGeneration);
}
