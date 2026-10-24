using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public sealed class PublishedWorkflowClient(IWorkflowCatalog catalog, LocalWorkflowRuntime runtime) : IPublishedWorkflowClient, IPublishedWorkflowRunControl
{
    public async Task<RunSnapshot> StartPublishedRunAsync(Guid revisionId, string input)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionAsync(published.Revision, input);
    }

    public async Task<RunSnapshot> StartPublishedPausedRunAsync(Guid revisionId, string input)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionPausedAsync(published.Revision, input);
    }

    public Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input) => runtime.StartPausedRunAsync(definition, input);
    public Task PauseRunAsync(Guid runId) => runtime.PauseRunAsync(runId);
    public Task ResumeRunAsync(Guid runId) => runtime.ResumeRunAsync(runId);

    public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) => runtime.StartRunAsync(definition, input);
    public Task<RunSnapshot?> GetRunAsync(Guid runId) => runtime.GetRunAsync(runId);
    public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => runtime.ListRunsAsync();
    public Task CancelRunAsync(Guid runId) => runtime.CancelRunAsync(runId);
}
