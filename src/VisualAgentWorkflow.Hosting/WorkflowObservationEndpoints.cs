using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public static class WorkflowObservationEndpoints
{
    /// <summary>Explicitly maps read-only evidence endpoints protected by a host-defined named policy.
    /// Register an IWorkflowRunObserver whose visible data matches the host's access model.
    /// No endpoints are mapped merely by registering the local runtime.</summary>
    public static RouteGroupBuilder MapWorkflowObservation(this IEndpointRouteBuilder endpoints, string prefix, string authorizationPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        var group = endpoints.MapGroup(prefix).RequireAuthorization(authorizationPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        group.MapGet("/runs", (IWorkflowRunObserver observer, CancellationToken ct) => observer.ListRunsAsync(ct));
        group.MapGet("/runs/{id:guid}", async (Guid id, IWorkflowRunObserver observer, CancellationToken ct) =>
            await observer.GetRunAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        group.MapGet("/runs/{id:guid}/reconciliation", async (Guid id, IWorkflowRunObserver observer, CancellationToken ct) =>
        {
            if (observer is not IWorkflowRunReconciliationObserver assessments) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            return await assessments.GetReconciliationAsync(id, ct) is { } report ? Results.Ok(report) : Results.NotFound();
        });
        group.MapGet("/runs/{id:guid}/events", async (Guid id, long? after, int? limit, IWorkflowRunObserver observer, CancellationToken ct) =>
        {
            if (after is < 0 || limit is < 1 or > 256) return Results.BadRequest();
            try
            {
                var page = await observer.GetEventsAsync(id, after ?? 0, limit ?? 256, ct);
                return page is null ? Results.NotFound() : Results.Ok(page);
            }
            catch (ArgumentOutOfRangeException) { return Results.BadRequest(); }
        });
        return group;
    }
}
