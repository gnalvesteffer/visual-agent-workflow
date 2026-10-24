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
            var definition = WorkflowJson.Deserialize(request.Definition.GetRawText());
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
            var definition = WorkflowJson.Deserialize(request.Definition.GetRawText());
            if (definition.WorkflowId != workflowId) return Results.BadRequest();
            var layout = WorkflowLayoutJson.Deserialize(request.Layout.GetRawText(), definition);
            return Results.Ok(await designs.SaveDesignAsync(definition, layout, request.ExpectedGeneration));
        });
        group.MapGet("/revisions/{revisionId:guid}/layout", async (Guid revisionId, IWorkflowCatalog catalog) =>
            catalog is IWorkflowDesignCatalog designs && await designs.GetRevisionLayoutAsync(revisionId) is { } layout
                ? Results.Ok(layout) : Results.NotFound());
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
            var run = await client.StartPublishedRunAsync(revisionId, request.Input);
            return Results.Accepted($"/api/runs/{run.Header.RunId}", run);
        });
        group.MapPost("/revisions/{revisionId:guid}/runs", async (Guid revisionId, PublishedRunRequest request, IPublishedWorkflowClient client) =>
        {
            if (request.Input is null) return Results.BadRequest();
            var run = await client.StartPublishedRunAsync(revisionId, request.Input);
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
    private sealed record PublishedRunRequest(string? Input);
}
