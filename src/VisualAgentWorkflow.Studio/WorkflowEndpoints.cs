using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

internal static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").AddEndpointFilter<CatalogErrorFilter>();
        group.MapGet("/workflows", async (IWorkflowCatalog catalog) => Results.Ok(await catalog.ListDraftsAsync()));
        group.MapGet("/workflows/{workflowId}", async (string workflowId, IWorkflowCatalog catalog) =>
            await catalog.GetDraftAsync(workflowId) is { } draft ? Results.Ok(draft) : Results.NotFound());
        group.MapPut("/workflows/{workflowId}/draft", async (string workflowId, SaveDraftRequest request, IWorkflowCatalog catalog) =>
        {
            if (request.Definition.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            var definition = WorkflowJson.DeserializeDraft(request.Definition.GetRawText());
            if (!string.Equals(definition.WorkflowId, workflowId, StringComparison.Ordinal)) return Results.BadRequest();
            return Results.Ok(await catalog.SaveDraftAsync(definition, request.ExpectedGeneration));
        });
        group.MapGet("/workflows/{workflowId}/design", async (string workflowId, IWorkflowCatalog catalog) =>
            catalog is IWorkflowDesignCatalog designs && await designs.GetDesignAsync(workflowId) is { } design
                ? Results.Ok(design) : Results.NotFound());
        group.MapPut("/workflows/{workflowId}/design", async (string workflowId, SaveDesignRequest request, IWorkflowCatalog catalog) =>
        {
            if (catalog is not IWorkflowDesignCatalog designs || request.Definition.ValueKind != JsonValueKind.Object ||
                request.Layout.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            var definition = WorkflowJson.DeserializeDraft(request.Definition.GetRawText());
            if (definition.WorkflowId != workflowId) return Results.BadRequest();
            var layout = WorkflowLayoutJson.Deserialize(request.Layout.GetRawText(), definition);
            return Results.Ok(await designs.SaveDesignAsync(definition, layout, request.ExpectedGeneration));
        });
        group.MapGet("/revisions/{revisionId:guid}/layout", async (Guid revisionId, IWorkflowCatalog catalog) =>
            catalog is IWorkflowDesignCatalog designs && await designs.GetRevisionLayoutAsync(revisionId) is { } layout
                ? Results.Ok(layout) : Results.NotFound());
        group.MapGet("/workflows/{workflowId}/package", async (string workflowId, IWorkflowCatalog catalog) =>
        {
            var design = catalog is IWorkflowDesignCatalog designs ? await designs.GetDesignAsync(workflowId) : null;
            var draft = design?.Draft ?? (catalog is IWorkflowDesignCatalog ? null : await catalog.GetDraftAsync(workflowId));
            return draft is null ? Results.NotFound() : Results.Text(WorkflowPackageJson.Serialize(draft.Definition,
                design?.Layout ?? WorkflowLayout.Create(draft.Definition)), "application/json");
        });
        group.MapGet("/revisions/{revisionId:guid}/package", async (Guid revisionId, IWorkflowCatalog catalog) =>
        {
            var revision = await catalog.GetRevisionAsync(revisionId);
            if (revision is null) return Results.NotFound();
            var presentation = catalog is IWorkflowDesignCatalog designs ? await designs.GetRevisionLayoutAsync(revisionId) : null;
            return Results.Text(WorkflowPackageJson.Serialize(revision.Revision.Definition,
                presentation ?? WorkflowLayout.Create(revision.Revision.Definition)), "application/json");
        });
        group.MapPost("/workflows/{workflowId}/publish", async (string workflowId, GenerationRequest request, IWorkflowCatalog catalog) =>
            Results.Ok(await catalog.PublishAsync(workflowId, request.ExpectedGeneration)));
        group.MapGet("/workflows/{workflowId}/revisions", async (string workflowId, IWorkflowCatalog catalog) =>
            Results.Ok(await catalog.ListRevisionsAsync(workflowId)));
        group.MapGet("/revisions/{revisionId:guid}", async (Guid revisionId, IWorkflowCatalog catalog) =>
            await catalog.GetRevisionAsync(revisionId) is { } revision ? Results.Ok(revision) : Results.NotFound());
        group.MapPost("/workflows/{workflowId}/restore", async (string workflowId, RevisionChangeRequest request, IWorkflowCatalog catalog) =>
            Results.Ok(await catalog.RestoreDraftAsync(workflowId, request.RevisionId, request.ExpectedGeneration)));
        group.MapPost("/workflows/{workflowId}/activate", async (string workflowId, RevisionChangeRequest request, IWorkflowCatalog catalog) =>
            Results.Ok(await catalog.ActivateAsync(workflowId, request.RevisionId, request.ExpectedGeneration)));
        group.MapGet("/workflows/{workflowId}/audit", async (string workflowId, IWorkflowCatalog catalog) =>
            Results.Ok(await catalog.ReadAuditAsync(workflowId)));
        group.MapPost("/workflows/{workflowId}/runs", async (string workflowId, PublishedRunRequest request, IWorkflowCatalog catalog, IPublishedWorkflowClient client) =>
        {
            if (request.Input is null) return Results.BadRequest();
            var draft = await catalog.GetDraftAsync(workflowId) ?? throw new KeyNotFoundException("Workflow not found.");
            if (draft.ActiveRevisionId is not { } revisionId) return Results.Conflict(new { error = "No published revision is active." });
            if (request.StartPaused && client is not IPublishedWorkflowRunControl) return Results.BadRequest();
            if (request.Parameters is not null && client is not IParameterizedPublishedWorkflowClient) return Results.BadRequest();
            var run = request.Parameters is { } parameters
                ? request.StartPaused
                    ? await ((IParameterizedPublishedWorkflowClient)client).StartPublishedPausedWithParametersAsync(revisionId, request.Input, parameters)
                    : await ((IParameterizedPublishedWorkflowClient)client).StartPublishedRunWithParametersAsync(revisionId, request.Input, parameters)
                : request.StartPaused
                    ? await ((IPublishedWorkflowRunControl)client).StartPublishedPausedRunAsync(revisionId, request.Input)
                    : await client.StartPublishedRunAsync(revisionId, request.Input);
            return Results.Accepted($"/api/runs/{run.Header.RunId}", run);
        });
        group.MapPost("/revisions/{revisionId:guid}/runs", async (Guid revisionId, PublishedRunRequest request, IPublishedWorkflowClient client) =>
        {
            if (request.Input is null) return Results.BadRequest();
            if (request.StartPaused && client is not IPublishedWorkflowRunControl) return Results.BadRequest();
            if (request.Parameters is not null && client is not IParameterizedPublishedWorkflowClient) return Results.BadRequest();
            var run = request.Parameters is { } parameters
                ? request.StartPaused
                    ? await ((IParameterizedPublishedWorkflowClient)client).StartPublishedPausedWithParametersAsync(revisionId, request.Input, parameters)
                    : await ((IParameterizedPublishedWorkflowClient)client).StartPublishedRunWithParametersAsync(revisionId, request.Input, parameters)
                : request.StartPaused
                    ? await ((IPublishedWorkflowRunControl)client).StartPublishedPausedRunAsync(revisionId, request.Input)
                    : await client.StartPublishedRunAsync(revisionId, request.Input);
            return Results.Accepted($"/api/runs/{run.Header.RunId}", run);
        });
    }

    private sealed class CatalogErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try { return await next(context); }
            catch (WorkflowConflictException conflict)
            {
                return Results.Conflict(new { error = "The workflow changed. Reload before saving.", conflict.ExpectedGeneration, conflict.ActualGeneration });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            { return Results.BadRequest(new { error = "Invalid definition, revision, or generation." }); }
        }
    }

    private sealed record SaveDesignRequest(JsonElement Definition, JsonElement Layout, long ExpectedGeneration);
    private sealed record SaveDraftRequest(JsonElement Definition, long ExpectedGeneration);
    private sealed record GenerationRequest(long ExpectedGeneration);
    private sealed record RevisionChangeRequest(Guid RevisionId, long ExpectedGeneration);
    private sealed record PublishedRunRequest(string? Input, bool StartPaused = false)
    {
        public Dictionary<string, string>? Parameters { get; init; }
    }
}
