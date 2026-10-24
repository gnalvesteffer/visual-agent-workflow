using System.Net;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio.Components;
using VisualAgentWorkflow.Studio;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.AgentFramework;

if (args.Contains("--mcp-demo-server", StringComparer.Ordinal))
{
    await DemoMcpServer.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("This unauthenticated prototype may run only in Development. Production hosting is not implemented.");
builder.WebHost.UseUrls("http://127.0.0.1:5099");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<INodeToolBroker>(new McpToolBroker(DemoMcpServer.ForCurrentExecutable(), new DemoToolPolicy()));
builder.Services.AddSingleton<IToolBindingCatalog>(services => services.GetRequiredService<INodeToolBroker>());
builder.Services.AddSingleton<IAgentNodeExecutor>(services => new AgentNodeExecutor(
    [DeterministicDemoChatClient.Registration()], services.GetRequiredService<INodeToolBroker>()));
builder.Services.AddSingleton<IAgentProviderCatalog>(services => services.GetRequiredService<IAgentNodeExecutor>());
builder.Services.AddSingleton(new FileRunJournal(Path.Combine(builder.Environment.ContentRootPath, ".local", "runs")));
builder.Services.AddSingleton<LocalWorkflowRuntime>();
builder.Services.AddSingleton<IWorkflowCatalog>(new SqliteWorkflowCatalog(Path.Combine(builder.Environment.ContentRootPath, ".local", "workflows.db")));
builder.Services.AddSingleton<PublishedWorkflowClient>();
builder.Services.AddSingleton<IPublishedWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddSingleton<IWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddHostedService(services => services.GetRequiredService<LocalWorkflowRuntime>());
var app = builder.Build();

// Development containment, not a production authentication boundary. Do not put behind a public proxy.
app.Use(async (context, next) =>
{
    var peer = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    if (peer is null || !IPAddress.IsLoopback(peer) || !localHost)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    // Check browser origins on WebSocket upgrades too, not only state-changing HTTP methods.
    var origin = context.Request.Headers.Origin.ToString();
    var expected = $"{context.Request.Scheme}://{context.Request.Host}";
    if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
        origin.Length > 0 && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseAntiforgery();
app.MapWorkflowEndpoints();
app.MapGet("/api/tool-bindings", async (IToolBindingCatalog catalog, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.ListBindingsAsync(cancellationToken)));
app.MapGet("/api/agent-providers", async (IAgentProviderCatalog catalog) => Results.Ok(await catalog.ListProvidersAsync()));
app.MapGet("/api/templates/default", () => Results.Text(WorkflowJson.Serialize(PipelineTemplates.Create("uppercase-reverse")), "application/json"));
app.MapPost("/api/runs", async (StartRunRequest request, IWorkflowClient client) =>
{
    try
    {
        if (request.Input is null || request.Definition.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "An input string and definition object are required." });
        var definition = WorkflowJson.Deserialize(request.Definition.GetRawText());
        var snapshot = await client.StartRunAsync(definition, request.Input);
        return Results.Accepted($"/api/runs/{snapshot.Header.RunId}", snapshot);
    }
    catch (Exception error) when (error is ArgumentException or JsonException)
    {
        return Results.BadRequest(new { error = "Invalid workflow definition or input. Only validated string pipelines are supported." });
    }
});
app.MapGet("/api/runs", async (IWorkflowClient client) => Results.Ok(await client.ListRunsAsync()));
app.MapGet("/api/runs/{id:guid}", async (Guid id, IWorkflowClient client) =>
    await client.GetRunAsync(id) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
app.MapGet("/api/runs/{id:guid}/events", async (Guid id, long? after, IWorkflowClient client) =>
{
    if (after < 0) return Results.BadRequest();
    var snapshot = await client.GetRunAsync(id);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot.Events.Where(e => e.Sequence > (after ?? 0)));
});
app.MapPost("/api/runs/{id:guid}/cancel", async (Guid id, IWorkflowClient client) =>
{
    if (await client.GetRunAsync(id) is null) return Results.NotFound();
    await client.CancelRunAsync(id);
    return Results.NoContent();
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public sealed record StartRunRequest(JsonElement Definition, string? Input);
