using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public sealed class PublishedWorkflowClient(IWorkflowCatalog catalog, LocalWorkflowRuntime runtime) : IPublishedWorkflowClient
{
    public async Task<RunSnapshot> StartPublishedRunAsync(Guid revisionId, string input)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionAsync(published.Revision, input);
    }

    public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) => runtime.StartRunAsync(definition, input);
    public Task<RunSnapshot?> GetRunAsync(Guid runId) => runtime.GetRunAsync(runId);
    public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => runtime.ListRunsAsync();
    public Task CancelRunAsync(Guid runId) => runtime.CancelRunAsync(runId);
}
