using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public sealed class PublishedWorkflowClient(IWorkflowCatalog catalog, LocalWorkflowRuntime runtime) : IPublishedWorkflowClient,
    IParameterizedPublishedWorkflowClient, IParameterizedWorkflowClient, IPublishedWorkflowRunControl, IWorkflowApprovalClient
{
    public async Task<RunSnapshot> StartPublishedRunAsync(Guid revisionId, string input)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionAsync(published.Revision, input);
    }

    public async Task<RunSnapshot> StartPublishedRunWithParametersAsync(Guid revisionId, string input,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionWithParametersAsync(published.Revision, input, parameters);
    }

    public async Task<RunSnapshot> StartPublishedPausedRunAsync(Guid revisionId, string input)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionPausedAsync(published.Revision, input);
    }

    public async Task<RunSnapshot> StartPublishedPausedWithParametersAsync(Guid revisionId, string input,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var published = await catalog.GetRevisionAsync(revisionId) ?? throw new KeyNotFoundException("Published revision not found.");
        return await runtime.StartRevisionWithParametersAsync(published.Revision, input, parameters, startPaused: true);
    }

    public Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input) =>
        StartDirectRunAsync(definition, input, null, startPaused: true);
    public Task<RunSnapshot> StartRunWithParametersAsync(WorkflowDefinition definition, string input,
        IReadOnlyDictionary<string, string>? parameters) => StartDirectRunAsync(definition, input, parameters);
    public Task<RunSnapshot> StartPausedRunWithParametersAsync(WorkflowDefinition definition, string input,
        IReadOnlyDictionary<string, string>? parameters) => StartDirectRunAsync(definition, input, parameters, startPaused: true);
    public Task PauseRunAsync(Guid runId) => runtime.PauseRunAsync(runId);
    public Task ResumeRunAsync(Guid runId) => runtime.ResumeRunAsync(runId);

    public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input) => StartDirectRunAsync(definition, input, null);

    private async Task<RunSnapshot> StartDirectRunAsync(WorkflowDefinition definition, string input,
        IReadOnlyDictionary<string, string>? parameters, bool startPaused = false)
    {
        var revision = WorkflowRevision.Create(definition, "local-developer");
        if (catalog is IWorkflowDesignCatalog designs &&
            await designs.GetDesignAsync(definition.WorkflowId) is { } saved &&
            WorkflowJson.ComputeDraftHash(saved.Draft.Definition) == WorkflowJson.ComputeDraftHash(definition))
        {
            revision = revision with { Layout = saved.Layout };
        }
        return await runtime.StartRevisionWithParametersAsync(revision, input, parameters, startPaused);
    }
    public Task<RunSnapshot?> GetRunAsync(Guid runId) => runtime.GetRunAsync(runId);
    public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => runtime.ListRunsAsync();
    public Task DecideApprovalAsync(Guid runId, Guid requestId, string actionHash, bool approve) => runtime.DecideApprovalAsync(runId, requestId, actionHash, approve);
    public Task CancelRunAsync(Guid runId) => runtime.CancelRunAsync(runId);
}
