using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app, string routePrefix = "/api")
    {
        var group = app.MapGroup(routePrefix).AddEndpointFilter<CatalogErrorFilter>();
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
            return Results.Text(WorkflowPackageJson.Serialize(revision.Revision,
                presentation ?? revision.Revision.Layout ?? WorkflowLayout.Create(revision.Revision.Definition)), "application/json");
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
            return Results.Accepted($"/api/runs/{run.Header.RunId}", new { runId = run.Header.RunId });
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

        // Create a new workflow from template or empty graph
        group.MapPost("/workflows", async (CreateWorkflowRequest request, IWorkflowCatalog catalog) =>
        {
            var workflowId = request.Name ?? Guid.NewGuid().ToString();
            var definition = WorkflowTemplates.Create("uppercase") with { WorkflowId = workflowId };
            if (!string.IsNullOrEmpty(request.Name))
                definition = definition with { DisplayName = request.Name };
            var draft = await catalog.SaveDraftAsync(definition, 0);
            return Results.Created($"/api/workflows/{workflowId}", new { workflowId, name = request.Name ?? workflowId });
        });

        // Add a node to the workflow graph
        group.MapPost("/workflows/{workflowId}/nodes", async (string workflowId, AddNodeRequest request, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            try
            {
                NodeDefinition node = new(request.Type, "1.0.0");
                
                // For string constants, set the value from configuration
                if (request.Type == "core.constant.string" && request.Configuration.HasValue && 
                    request.Configuration.Value.ValueKind == JsonValueKind.Object)
                {
                    if (request.Configuration.Value.TryGetProperty("value", out var valueProp))
                    {
                        node = node with { StringConstant = new(valueProp.GetString() ?? "") };
                    }
                }
                
                var updated = GraphEditing.AddUnconnectedNode(draft.Definition, node);
                var saved = await catalog.SaveDraftAsync(updated, draft.Generation);
                var newId = updated.Nodes.Keys.Except(draft.Definition.Nodes.Keys, StringComparer.Ordinal).Single();
                return Results.Created($"/api/workflows/{workflowId}/nodes/{newId}", new { nodeId = newId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"NODE ERROR: {ex.GetType().Name}: {ex.Message}");
                return Results.BadRequest(new { error = "Failed to add node.", details = ex.Message });
            }
        });

        // Update a node's configuration
        group.MapPut("/workflows/{workflowId}/nodes/{nodeId}", async (string workflowId, string nodeId, UpdateNodeRequest request, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            try
            {
                var updated = GraphEditing.ReplaceNode(draft.Definition, nodeId, new NodeDefinition(draft.Definition.Nodes[nodeId].Type, "1.0.0"));
                await catalog.SaveDraftAsync(updated, draft.Generation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UPDATE NODE ERROR: {ex.GetType().Name}: {ex.Message}");
                return Results.BadRequest(new { error = "Failed to update node.", details = ex.Message });
            }

            return Results.Ok();
        });

        // Remove a node from the graph
        group.MapDelete("/workflows/{workflowId}/nodes/{nodeId}", async (string workflowId, string nodeId, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            try
            {
                var updated = GraphEditing.RemoveNode(draft.Definition, nodeId);
                await catalog.SaveDraftAsync(updated, draft.Generation);
            }
            catch (Exception)
            {
                return Results.NotFound(new { error = "Node not found." });
            }

            return Results.Ok();
        });

        // Connect two nodes with an edge
        group.MapPost("/workflows/{workflowId}/edges", async (string workflowId, AddEdgeRequest request, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            try
            {
                var updated = GraphEditing.ConnectPins(draft.Definition, request.SourceNode, request.SourcePort, request.TargetNode, request.TargetPort);
                var saved = await catalog.SaveDraftAsync(updated, draft.Generation);
                // Find the new connection ID by comparing old vs new connections
                var oldConnectionIds = draft.Definition.Connections.Keys.ToHashSet(StringComparer.Ordinal);
                var newConnectionIds = updated.Connections.Keys.Except(oldConnectionIds, StringComparer.Ordinal).ToList();
                string edgeId;
                if (newConnectionIds.Count > 0)
                {
                    edgeId = "data:" + newConnectionIds[0];
                }
                else
                {
                    // Must be an execution connection
                    var oldExecIds = draft.Definition.ExecutionConnections?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
                    var newExecIds = updated.ExecutionConnections!.Keys.Except(oldExecIds, StringComparer.Ordinal).ToList();
                    edgeId = "execution:" + newExecIds[0];
                }
                return Results.Created($"/api/workflows/{workflowId}/edges/{edgeId}", new { edgeId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EDGE ERROR: {ex.GetType().Name}: {ex.Message}");
                return Results.BadRequest(new { error = "Failed to connect pins.", details = ex.Message });
            }
        });

        // Remove an edge from the graph
        group.MapDelete("/workflows/{workflowId}/edges/{edgeId}", async (string workflowId, string edgeId, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            try
            {
                var updated = GraphEditing.Disconnect(draft.Definition, edgeId);
                await catalog.SaveDraftAsync(updated, draft.Generation);
            }
            catch (Exception)
            {
                return Results.NotFound(new { error = "Edge not found." });
            }

            return Results.Ok();
        });

        // Validate workflow without publishing
        group.MapPost("/workflows/{workflowId}/validate", async (string workflowId, IWorkflowCatalog catalog) =>
        {
            var draft = await catalog.GetDraftAsync(workflowId);
            if (draft is null) return Results.NotFound();

            var issues = WorkflowValidator.Validate(draft.Definition).Select(issue => new { code = issue.Code, path = issue.Path, message = issue.Message }).ToList();
            return Results.Ok(new { valid = issues.Count == 0, issues });
        });

        // List available node families
        group.MapGet("/nodes", () =>
        {
            var types = WorkflowNodeContractCatalog.All.Select(c => c.Type).OrderBy(t => t).ToList();
            return Results.Ok(types);
        });

        // Get detailed pin contracts for a node family
        group.MapGet("/nodes/{family}", (string family) =>
        {
            if (!WorkflowNodeContractCatalog.TryGet(family, out var contract))
                return Results.NotFound(new { error = "Unknown node family." });

            var sampleNode = new NodeDefinition(family, "1.0.0");
            var ports = WorkflowPorts.Explicit(sampleNode);
            var inputs = ports.Where(p => p.Direction == WorkflowPortDirection.Input).Select(p => new { id = p.Id, name = p.DisplayName ?? p.Id, dataType = p.DataType, required = p.Required }).ToList();
            var outputs = ports.Where(p => p.Direction == WorkflowPortDirection.Output).Select(p => new { id = p.Id, name = p.DisplayName ?? p.Id, dataType = p.DataType }).ToList();
            return Results.Ok(new { name = family, inputs, outputs });
        });

        // Run management endpoints
        group.MapPost("/runs/{runId:guid}/pause", async (Guid runId, IWorkflowRunControl control) =>
        {
            try
            {
                await control.PauseRunAsync(runId);
                return Results.Accepted($"/api/runs/{runId}");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Run not found." });
            }
        });

        group.MapPost("/runs/{runId:guid}/resume", async (Guid runId, IWorkflowRunControl control) =>
        {
            try
            {
                await control.ResumeRunAsync(runId);
                return Results.Accepted($"/api/runs/{runId}");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Run not found." });
            }
        });

        group.MapPost("/runs/{runId:guid}/cancel", async (Guid runId, IWorkflowClient client) =>
        {
            try
            {
                await client.CancelRunAsync(runId);
                return Results.Accepted($"/api/runs/{runId}");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Run not found." });
            }
        });

        group.MapGet("/runs", async (IWorkflowClient client) =>
        {
            var runs = await client.ListRunsAsync();
            var summaries = runs.Select(r => new { runId = r.Header.RunId, status = r.Status, createdAt = r.Header.CreatedAt }).ToList();
            return Results.Ok(summaries);
        });

        group.MapGet("/runs/{runId:guid}", async (Guid runId, IWorkflowClient client) =>
        {
            var run = await client.GetRunAsync(runId);
            if (run is null) return Results.NotFound(new { error = "Run not found." });
            return Results.Ok(new { runId = run.Header.RunId, status = run.Status, eventCount = run.Events.Length });
        });

        group.MapGet("/runs/{runId:guid}/events", async (Guid runId, IWorkflowClient client) =>
        {
            var run = await client.GetRunAsync(runId);
            if (run is null) return Results.NotFound(new { error = "Run not found." });
            var events = run.Events.Select(e => new { sequence = e.Sequence, kind = e.Kind, nodeId = e.NodeId, timestamp = e.Timestamp }).ToList();
            return Results.Ok(events);
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
            catch (VisualAgentWorkflow.Core.WorkflowAdmissionException admission)
            {
                var issues = admission.Report.Issues.Select(issue => new
                {
                    kind = issue.Requirement.Kind,
                    id = issue.Requirement.Id,
                    issueKind = issue.Kind.ToString(),
                    message = issue.Message
                }).ToList();
                return Results.BadRequest(new
                {
                    error = "The workflow cannot start because required host dependencies are unavailable.",
                    missingDependencies = issues
                });
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CatalogErrorFilter>>()
                    .LogWarning("Rejected workflow catalog request: {Reason}", error.Message);
                return Results.BadRequest(new { error = "Invalid definition, revision, or generation." });
            }
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
    private sealed record CreateWorkflowRequest(string? Name, string? Template);
    private sealed record AddNodeRequest(string Type, JsonElement? Configuration);
    private sealed record UpdateNodeRequest(JsonElement Configuration);
    private sealed record AddEdgeRequest(string SourceNode, string SourcePort, string TargetNode, string TargetPort);
}
